using System;
using System.IO;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Core.Items;
using Core.Unit;
using Evergine.Bindings.Imgui;
using ImGuiVector2 = Evergine.Mathematics.Vector2;
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

    private readonly Action<
        string[],
        BodyPartDefinition[],
        UnitDefinition,
        float[],
        HitRegionPartBinding[][],
        UnitAnatomyMarker[][],
        UnitAnatomyVolume[],
        UnitStartingEquipment[]> _saveRequested;
    private readonly Action<UnitArchetypeAsset> _projectAssetSaved;
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
    private readonly RectangleShape _detachParentButton = new(new Vector2f(1f, 26f))
    {
        FillColor = new Color(76, 43, 47, 255),
        OutlineColor = new Color(131, 76, 82, 255),
        OutlineThickness = 1f
    };
    private readonly RectangleShape _detachAllParentsButton = new(new Vector2f(1f, 25f))
    {
        FillColor = new Color(76, 43, 47, 255),
        OutlineColor = new Color(131, 76, 82, 255),
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
    private readonly RectangleShape _viewNormalizationButton = new(new Vector2f(1f, 26f))
    {
        FillColor = new Color(42, 57, 72, 255),
        OutlineColor = new Color(82, 104, 127, 255),
        OutlineThickness = 1f
    };
    private readonly RectangleShape _resetCenterButton = new(new Vector2f(1f, 26f))
    {
        FillColor = new Color(42, 57, 72, 255),
        OutlineColor = new Color(82, 104, 127, 255),
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
    private readonly RectangleShape _copyPartButton = new(new Vector2f(1f, 25f))
    {
        FillColor = new Color(47, 65, 82, 255),
        OutlineColor = new Color(79, 103, 127, 255),
        OutlineThickness = 1f
    };
    private readonly RectangleShape _symmetryToolButton = new(new Vector2f(1f, 25f))
    {
        FillColor = new Color(52, 74, 64, 255),
        OutlineColor = new Color(90, 128, 105, 255),
        OutlineThickness = 1f
    };
    private readonly RectangleShape _symmetryClearButton = new(new Vector2f(1f, 26f))
    {
        FillColor = new Color(76, 43, 47, 255),
        OutlineColor = new Color(131, 76, 82, 255),
        OutlineThickness = 1f
    };
    private readonly RectangleShape _renamePartButton = new(new Vector2f(1f, 26f))
    {
        FillColor = new Color(47, 65, 82, 255),
        OutlineColor = new Color(79, 103, 127, 255),
        OutlineThickness = 1f
    };

    private readonly Text? _title;
    private readonly Text? _saveLabel;
    private readonly Text? _loadLabel;
    private readonly Text? _exportLabel;
    private readonly Text? _defaultsLabel;
    private readonly Text? _reloadLabel;
    private readonly Text? _parentLabel;
    private readonly Text? _detachParentLabel;
    private readonly Text? _detachAllParentsLabel;
    private readonly Text? _allSymmetryLabel;
    private readonly Text? _gridLabel;
    private readonly Text? _gridStepLabel;
    private readonly Text? _surfaceSnapLabel;
    private readonly Text? _viewNormalizationLabel;
    private readonly Text? _resetCenterLabel;
    private readonly Text? _addRegionLabel;
    private readonly Text? _addAllFormsLabel;
    private readonly Text? _removeRegionLabel;
    private readonly Text? _copyPartLabel;
    private readonly Text? _symmetryToolLabel;
    private readonly Text? _symmetryClearLabel;
    private readonly Text? _renamePartLabel;
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
    private UnitDefinition _definition = new(UnitType.Colonist, UnitBodyType.Colonist, 2.8f, 0.35f, 0.60f, 1.75f, 20f, 110f);
    private bool _editingProjectAsset;
    private string _projectUnitPath = string.Empty;
    private string _projectUnitId = string.Empty;
    private string _projectUnitDisplayName = "Новый юнит";
    private string _projectUnitDescription = string.Empty;
    private ushort _projectRuntimeTypeId;
    private float[] _hitHalfHeights = Array.Empty<float>();
    private HitRegionPartBinding[][] _tissuesByRegion = Array.Empty<HitRegionPartBinding[]>();
    private UnitAnatomyMarker[][] _markersByRegion = Array.Empty<UnitAnatomyMarker[]>();
    private UnitAnatomyVolume[] _organs = Array.Empty<UnitAnatomyVolume>();
    private UnitStartingEquipment[] _startingEquipment = Array.Empty<UnitStartingEquipment>();
    private int _selectedOrgan;
    private int _selectedEquipment;
    private int _selectedTissue;
    private int _selectedMarker;
    private bool _showUnitSettings;
    private bool _showOrganSettings;
    private bool _showTissueSettings;
    private bool _showEquipmentSettings;
    private NumericsVector3[] _worldPositions = Array.Empty<NumericsVector3>();
    private Quaternion[] _worldOrientations = Array.Empty<Quaternion>();
    private ProjectedPart[][] _projected = new ProjectedPart[3][];
    private ProjectionLayout[] _projectionLayouts = new ProjectionLayout[3];
    private readonly float[] _projectionZoom = { 1f, 1f, 1f };
    private readonly float[] _projectionPanX = new float[3];
    private readonly float[] _projectionPanY = new float[3];
    private int _panningProjection = -1;
    private bool _symmetryToolActive;
    private int _symmetrySourcePart = -1;
    private bool _parentSelectionActive;
    private int _parentSourcePart = -1;
    private bool _editingPartName;
    private string _partNameEditBuffer = string.Empty;
    private int _editingProperty = -1;
    private string _propertyEditBuffer = string.Empty;

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
    private bool _normalizeViewsEnabled;
    private float _normalizedPixelsPerUnit = 1f;
    private float _propertiesScrollOffset;
    private float _propertiesMaxScroll;
    private float _propertiesViewportTop;
    private float _propertiesViewportBottom;
    private readonly float[] _gridSteps = { 0.0005f, 0.001f, 0.0025f, 0.005f, 0.01f, 0.025f, 0.05f, 0.1f, 0.2f };
    private int _gridStepIndex = 3;
    private string _status = "Перетаскивание привязано к сетке. Настрой углы X/Y/Z в параметрах.";
    private bool _disposed;
    private ImGuiSfmlBackend? _imguiBackend;
    private readonly byte[] _imguiPartNameBuffer = new byte[256];
    private readonly byte[] _imguiEquipmentNameBuffer = new byte[256];
    private readonly byte[] _imguiAssetIdBuffer = new byte[96];
    private readonly byte[] _imguiAssetNameBuffer = new byte[256];
    private readonly byte[] _imguiAssetDescriptionBuffer = new byte[512];
    private string _imguiAssetIdValue = string.Empty;
    private string _imguiAssetNameValue = string.Empty;
    private string _imguiAssetDescriptionValue = string.Empty;
    private int _imguiNamePartIndex = -1;
    private int _imguiNameEquipmentIndex = -1;

    public bool IsOpen { get; private set; }

    public UnitBodyRigEditorOverlay(
        Action<
            string[],
            BodyPartDefinition[],
            UnitDefinition,
            float[],
            HitRegionPartBinding[][],
            UnitAnatomyMarker[][],
            UnitAnatomyVolume[],
            UnitStartingEquipment[]> saveRequested,
        Action<UnitArchetypeAsset> projectAssetSaved)
    {
        _saveRequested = saveRequested ?? throw new ArgumentNullException(nameof(saveRequested));
        _projectAssetSaved = projectAssetSaved ?? throw new ArgumentNullException(nameof(projectAssetSaved));
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
            _detachParentLabel = new Text(_font, "ОТВЯЗАТЬ В КОРЕНЬ", 8)
            {
                FillColor = new Color(243, 225, 227, 255),
                Style = Text.Styles.Bold
            };
            _detachAllParentsLabel = new Text(_font, "ОТВЯЗАТЬ ВСЕ ЧАСТИ", 8)
            {
                FillColor = new Color(243, 225, 227, 255),
                Style = Text.Styles.Bold
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
            _viewNormalizationLabel = new Text(_font, "ОБЩИЙ МАСШТАБ: ВЫКЛ", 8)
            {
                FillColor = new Color(232, 240, 248, 255),
                Style = Text.Styles.Bold
            };
            _resetCenterLabel = new Text(_font, "К ЦЕНТРУ ТЕЛА (0,0,0)", 8)
            {
                FillColor = new Color(232, 240, 248, 255),
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
            _copyPartLabel = new Text(_font, "КОПИРОВАТЬ ЧАСТЬ", 9)
            {
                FillColor = new Color(224, 233, 242, 255),
                Style = Text.Styles.Bold
            };
            _symmetryToolLabel = new Text(_font, "СИММЕТРИЯ: ВЫБРАТЬ ПАРУ", 8)
            {
                FillColor = new Color(237, 246, 241, 255),
                Style = Text.Styles.Bold
            };
            _symmetryClearLabel = new Text(_font, "СНЯТЬ СИММЕТРИЮ У ПАРЫ", 8)
            {
                FillColor = new Color(243, 225, 227, 255),
                Style = Text.Styles.Bold
            };
            _renamePartLabel = new Text(_font, "ПЕРЕИМЕНОВАТЬ ЧАСТЬ", 8)
            {
                FillColor = new Color(224, 233, 242, 255),
                Style = Text.Styles.Bold
            };
            _helpText = new Text(_font,
                "Колесо — прокрутка вида; Ctrl+колесо — масштаб; средняя кнопка — таскать. Число — ручной ввод. Привязка справа: выбери родителя, затем часть в списке; для отвязки есть отдельные кнопки.",
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
        try
        {
            _imguiBackend ??= new ImGuiSfmlBackend();
        }
        catch (Exception exception)
        {
            SetStatus($"Не удалось создать ImGui: {exception.Message}");
            return;
        }

        UnitArchetypeProfile archetype = UnitAnatomyCatalog.Get(UnitType.Colonist);
        _definition = archetype.Definition;
        _editingProjectAsset = false;
        _projectUnitPath = string.Empty;
        _projectUnitId = string.Empty;
        _projectUnitDisplayName = "Колонист";
        _projectUnitDescription = string.Empty;
        _projectRuntimeTypeId = 0;
        _imguiAssetIdValue = string.Empty;
        _imguiAssetNameValue = string.Empty;
        _imguiAssetDescriptionValue = string.Empty;
        _showUnitSettings = false;
        _showOrganSettings = false;
        _showTissueSettings = false;
        _showEquipmentSettings = false;
        UnitAnatomyProfile anatomy = archetype.Anatomy;
        _organs = anatomy.Volumes.ToArray();
        _startingEquipment = archetype.StartingEquipment.ToArray();
        _selectedEquipment = 0;
        _selectedOrgan = 0;
        _selectedTissue = 0;
        _selectedMarker = 0;
        // Open from the currently applied profile, not unsaved edits from a previous session.
        _regionIds = Array.Empty<string>();
        _parts = Array.Empty<BodyPartDefinition>();
        _hitHalfHeights = Array.Empty<float>();
        _tissuesByRegion = Array.Empty<HitRegionPartBinding[]>();
        _markersByRegion = Array.Empty<UnitAnatomyMarker[]>();
        ReadOnlySpan<UnitAnatomyNode> nodes = anatomy.Nodes;
        if (!anatomy.HasSharedShapes || nodes.Length == 0 || nodes.Length > MaxParts)
        {
            SetStatus($"Невозможно открыть риг: допустимо от 1 до {MaxParts} регионов.");
            return;
        }

        var ids = new string[nodes.Length];
        for (int i = 0; i < nodes.Length; i++)
            ids[i] = nodes[i].Id;

        if (!SetRigData(ids, anatomy.BodyParts.ToArray(), setDefaults: true, setOpenedSnapshot: true))
            return;

        _selectedProperty = 0;
        _draggingProperty = -1;
        _draggingProjection = -1;
        _draggingPart = false;
        _editingProperty = -1;
        _editingPartName = false;
        _partNameEditBuffer = string.Empty;
        _imguiNamePartIndex = -1;
        Array.Fill(_imguiPartNameBuffer, (byte)0);
        _symmetryToolActive = false;
        _symmetrySourcePart = -1;
        _parentSelectionActive = false;
        _parentSourcePart = -1;
        _surfaceSnapTargetPart = -1;
        _surfaceSnapProjection = -1;
        _hasSurfaceSnapDirection = false;
        IsOpen = true;

        bool clearedBindings = DetachAllParentsPreservingLayout();
        _defaultParts = (BodyPartDefinition[])_parts.Clone();
        _openedRegionIds = (string[])_regionIds.Clone();
        _openedParts = (BodyPartDefinition[])_parts.Clone();
        if (clearedBindings)
            Save();

        _status = clearedBindings
            ? $"Удалены все привязки: {_parts.Length} независимых частей. Положение сохранено; риг записан в файлы игры."
            : $"Загружено {_parts.Length} независимых частей. Родителей можно назначать инструментом справа.";
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

        string[] previousIds = _regionIds;
        HitRegionPartBinding[][] previousTissues = _tissuesByRegion;
        UnitAnatomyMarker[][] previousMarkers = _markersByRegion;
        float[] previousHalfHeights = _hitHalfHeights;
        UnitAnatomyProfile sourceAnatomy = UnitAnatomyCatalog.Get(UnitType.Colonist).Anatomy;

        _regionIds = (string[])regionIds.Clone();
        _parts = (BodyPartDefinition[])parts.Clone();
        _hitHalfHeights = new float[_regionIds.Length];
        _tissuesByRegion = new HitRegionPartBinding[_regionIds.Length][];
        _markersByRegion = new UnitAnatomyMarker[_regionIds.Length][];
        for (int i = 0; i < _regionIds.Length; i++)
        {
            int previousIndex = Array.IndexOf(previousIds, _regionIds[i]);
            if (previousIndex >= 0 &&
                previousIndex < previousTissues.Length &&
                previousIndex < previousMarkers.Length &&
                previousIndex < previousHalfHeights.Length &&
                previousTissues[previousIndex].Length > 0)
            {
                _tissuesByRegion[i] = (HitRegionPartBinding[])previousTissues[previousIndex].Clone();
                _markersByRegion[i] = (UnitAnatomyMarker[])previousMarkers[previousIndex].Clone();
                _hitHalfHeights[i] = previousHalfHeights[previousIndex];
                continue;
            }

            int sourceIndex = sourceAnatomy.FindRegion(_regionIds[i]);
            if (sourceIndex >= 0)
            {
                _tissuesByRegion[i] = sourceAnatomy.GetTissues(sourceIndex).ToArray();
                _markersByRegion[i] = sourceAnatomy.GetMarkers(sourceIndex).ToArray();
                _hitHalfHeights[i] = sourceAnatomy.GetHitHalfHeight(sourceIndex);
                continue;
            }

            string tissueId = $"{_regionIds[i]}.tissue";
            string parentTissueId = _parts[i].ParentIndex >= 0 &&
                _parts[i].ParentIndex < i &&
                _tissuesByRegion[_parts[i].ParentIndex] != null &&
                _tissuesByRegion[_parts[i].ParentIndex].Length > 0
                    ? _tissuesByRegion[_parts[i].ParentIndex][0].Id
                    : string.Empty;
            _tissuesByRegion[i] = new[]
            {
                new HitRegionPartBinding(
                    UnitHealthPartId.WholeBody,
                    kind: UnitHealthPartKind.BodyPart,
                    maxHitPoints: 30f,
                    id: tissueId,
                    parentId: string.IsNullOrWhiteSpace(parentTissueId) ? null : parentTissueId)
            };
            _markersByRegion[i] = new[]
            {
                new UnitAnatomyMarker(
                    "target",
                    UnitAnatomyMarkerRole.Target | UnitAnatomyMarkerRole.Aim | UnitAnatomyMarkerRole.Vision)
            };
            _hitHalfHeights[i] = MathF.Max(0.001f, MathF.Abs(_parts[i].Scale.Z));
        }

        PruneInvalidOrgans();
        _selectedTissue = 0;
        _selectedMarker = 0;
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
        _panningProjection = -1;
        _surfaceSnapTargetPart = -1;
        _surfaceSnapProjection = -1;
        _hasSurfaceSnapDirection = false;
        _activeProjection = -1;
    }

    public void SetStatus(string status) => _status = status ?? string.Empty;

    public bool HandleMousePressed(Vector2i position, Mouse.Button button = Mouse.Button.Left)
    {
        if (!IsOpen)
            return false;

        _imguiBackend?.MousePosition(position.X, position.Y);
        _imguiBackend?.MouseButton(button, true);
        if (IsImGuiUiPoint(position))
            return true;
        if (_imguiBackend?.WantCaptureMouse == true)
            return true;

        if (button == Mouse.Button.Middle)
        {
            for (int projection = 0; projection < _previewPanels.Length; projection++)
            {
                if (!_previewPanels[projection].GetGlobalBounds().Contains(position))
                    continue;

                _activeProjection = projection;
                _panningProjection = projection;
                _draggingPart = false;
                _draggingProjection = -1;
                _draggingProperty = -1;
                _lastMouseX = position.X;
                _lastMouseY = position.Y;
                _status = $"Перемещение вида «{ProjectionName(projection)}». Отпусти среднюю кнопку для завершения.";
                return true;
            }

            return true;
        }

        if (button != Mouse.Button.Left)
            return true;

        if (_editingPartName)
        {
            _editingPartName = false;
            _status = "Переименование отменено.";
        }

        _editingProperty = -1;

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

        if (_symmetryToolButton.GetGlobalBounds().Contains(position))
        {
            _parentSelectionActive = false;
            _parentSourcePart = -1;
            _symmetryToolActive = !_symmetryToolActive;
            _symmetrySourcePart = _symmetryToolActive ? _selectedPart : -1;
            _status = _symmetryToolActive
                ? $"Исходная часть: «{PartName(_regionIds[_symmetrySourcePart])}». Выбери парную строку; клик по исходной строке снимает симметрию."
                : "Инструмент симметрии закрыт.";
            return true;
        }

        if (_copyPartButton.GetGlobalBounds().Contains(position))
        {
            CopySelectedPart();
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

        if (_detachAllParentsButton.GetGlobalBounds().Contains(position))
        {
            bool changed = DetachAllParentsPreservingLayout();
            _defaultParts = (BodyPartDefinition[])_parts.Clone();
            _openedRegionIds = (string[])_regionIds.Clone();
            _openedParts = (BodyPartDefinition[])_parts.Clone();
            if (changed)
                Save();
            _status = changed
                ? "Все родительские связи удалены. Положение и ориентация частей сохранены."
                : "Все части уже независимы.";
            return true;
        }

        if (IsPropertiesItemVisible(_parentButton.Position.Y, _parentButton.Size.Y) &&
            _parentButton.GetGlobalBounds().Contains(position))
        {
            _symmetryToolActive = false;
            _symmetrySourcePart = -1;
            _parentSelectionActive = !_parentSelectionActive;
            _parentSourcePart = _parentSelectionActive ? _selectedPart : -1;
            _status = _parentSelectionActive
                ? $"Выбор родителя для «{PartName(_regionIds[_parentSourcePart])}»: выбери в списке слева часть, которая стоит выше."
                : "Выбор родителя отменён.";
            return true;
        }

        if (IsPropertiesItemVisible(_detachParentButton.Position.Y, _detachParentButton.Size.Y) &&
            _detachParentButton.GetGlobalBounds().Contains(position))
        {
            bool changed = DetachPartPreservingLayout(_selectedPart);
            if (changed)
                Save();
            _status = changed
                ? $"«{PartName(_regionIds[_selectedPart])}» отвязана и осталась на прежнем месте."
                : "Эта часть уже независима.";
            RefreshProjectedGeometry();
            return true;
        }

        if (IsPropertiesItemVisible(_allSymmetryButton.Position.Y, _allSymmetryButton.Size.Y) &&
            _allSymmetryButton.GetGlobalBounds().Contains(position))
        {
            bool enable = !_symmetryEnabled[0] || !_symmetryEnabled[1] || !_symmetryEnabled[2];
            Array.Fill(_symmetryEnabled, enable);
            _status = enable ? "Симметрия включена во всех проекциях." : "Симметрия выключена во всех проекциях.";
            return true;
        }

        if (IsPropertiesItemVisible(_gridButton.Position.Y, _gridButton.Size.Y) &&
            _gridButton.GetGlobalBounds().Contains(position))
        {
            _gridEnabled = !_gridEnabled;
            _status = _gridEnabled
                ? $"Сетка и привязка включены. Шаг: {_gridSteps[_gridStepIndex]:0.000}."
                : "Сетка и привязка отключены.";
            return true;
        }

        if (IsPropertiesItemVisible(_gridStepButton.Position.Y, _gridStepButton.Size.Y) &&
            _gridStepButton.GetGlobalBounds().Contains(position))
        {
            _gridStepIndex = (_gridStepIndex + 1) % _gridSteps.Length;
            _status = $"Шаг сетки: {_gridSteps[_gridStepIndex]:0.####}. Перемещение привязано к этому шагу.";
            return true;
        }

        if (IsPropertiesItemVisible(_surfaceSnapButton.Position.Y, _surfaceSnapButton.Size.Y) &&
            _surfaceSnapButton.GetGlobalBounds().Contains(position))
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

        if (IsPropertiesItemVisible(_viewNormalizationButton.Position.Y, _viewNormalizationButton.Size.Y) &&
            _viewNormalizationButton.GetGlobalBounds().Contains(position))
        {
            _normalizeViewsEnabled = !_normalizeViewsEnabled;
            _status = _normalizeViewsEnabled
                ? "Общий масштаб включён: одна единица мира имеет одинаковый размер во всех проекциях."
                : "Общий масштаб выключен: каждый вид подбирает масштаб отдельно.";
            return true;
        }

        if (IsPropertiesItemVisible(_resetCenterButton.Position.Y, _resetCenterButton.Size.Y) &&
            _resetCenterButton.GetGlobalBounds().Contains(position))
        {
            ResetSelectedPartToBodyCenter();
            return true;
        }

        if (IsPropertiesItemVisible(_symmetryClearButton.Position.Y, _symmetryClearButton.Size.Y) &&
            _symmetryClearButton.GetGlobalBounds().Contains(position))
        {
            ClearSymmetryPair(_selectedPart);
            RefreshProjectedGeometry();
            _status = $"Симметрия для «{PartName(_regionIds[_selectedPart])}» снята. Ctrl+S — сохранить.";
            return true;
        }

        if (IsPropertiesItemVisible(_renamePartButton.Position.Y, _renamePartButton.Size.Y) &&
            _renamePartButton.GetGlobalBounds().Contains(position))
        {
            _editingPartName = true;
            _partNameEditBuffer = _parts[_selectedPart].DisplayName ?? string.Empty;
            _status = "Введи новое имя, Enter — применить, Esc — отменить. Поддерживается русский текст; Ctrl+S — сохранить.";
            return true;
        }

        for (int projection = 0; projection < _symmetryButtons.Length; projection++)
        {
            if (!IsPropertiesItemVisible(_symmetryButtons[projection].Position.Y, _symmetryButtons[projection].Size.Y) ||
                !_symmetryButtons[projection].GetGlobalBounds().Contains(position))
                continue;

            _symmetryEnabled[projection] = !_symmetryEnabled[projection];
            _status = $"Симметрия «{new[] { "спереди", "сбоку", "сверху" }[projection]}»: " +
                (_symmetryEnabled[projection] ? "включена." : "выключена.");
            return true;
        }

        if (IsPropertiesItemVisible(_primitiveButton.Position.Y, _primitiveButton.Size.Y) &&
            _primitiveButton.GetGlobalBounds().Contains(position))
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

            if (_parentSelectionActive)
            {
                int childIndex = _parentSourcePart;
                if (itemIndex == childIndex)
                {
                    _parentSelectionActive = false;
                    _parentSourcePart = -1;
                    _status = "Выбор родителя отменён.";
                    return true;
                }

                if (itemIndex >= childIndex)
                {
                    _status = $"«{PartName(_regionIds[itemIndex])}» расположена ниже дочерней части. Родитель должен быть выше в списке.";
                    return true;
                }

                BindPartToParentPreservingLayout(childIndex, itemIndex);
                _selectedPart = childIndex;
                _parentSelectionActive = false;
                _parentSourcePart = -1;
                RefreshProjectedGeometry();
                _status = $"Назначен родитель «{PartName(_regionIds[itemIndex])}». Положение не изменилось; Ctrl+S — сохранить.";
                return true;
            }

            if (_symmetryToolActive)
            {
                if (itemIndex == _symmetrySourcePart)
                {
                    ClearSymmetryPair(itemIndex);
                    _selectedPart = itemIndex;
                    _status = $"Симметрия для «{PartName(_regionIds[itemIndex])}» снята.";
                }
                else
                {
                    int sourceIndex = _symmetrySourcePart;
                    SetSymmetryPair(sourceIndex, itemIndex);
                    _selectedPart = itemIndex;
                    _status = $"«{PartName(_regionIds[sourceIndex])}» и «{PartName(_regionIds[itemIndex])}» зеркально связаны; размеры синхронизированы.";
                }

                _symmetryToolActive = false;
                _symmetrySourcePart = -1;
                EnsureSelectionVisible();
                RefreshProjectedGeometry();
                return true;
            }

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
            RectangleShape track = _sliderTracks[i];
            if (!IsPropertiesItemVisible(track.Position.Y - 23f, 42f))
                continue;

            if (_sliderValues[i] != null && _sliderValues[i]!.GetGlobalBounds().Contains(position))
            {
                _selectedProperty = i;
                _activeProjection = -1;
                _draggingProperty = -1;
                _propertyEditBuffer = GetProperty(i).Value.ToString("0.###", CultureInfo.InvariantCulture);
                _editingProperty = i;
                _status = "Ввод числа: цифры, точка/запятая и минус. Enter — применить, Esc — отменить.";
                return true;
            }

            if (!track.GetGlobalBounds().Contains(position))
                continue;

            _selectedProperty = i;
            _activeProjection = -1;
            _editingProperty = -1;
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

        _imguiBackend?.MousePosition(position.X, position.Y);
        if (IsImGuiUiPoint(position) && _panningProjection < 0 && !_draggingPart)
            return true;

        if (_panningProjection >= 0)
        {
            _projectionPanX[_panningProjection] += position.X - _lastMouseX;
            _projectionPanY[_panningProjection] += position.Y - _lastMouseY;
            _lastMouseX = position.X;
            _lastMouseY = position.Y;
            return true;
        }

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

    public bool HandleMouseReleased(Mouse.Button button = Mouse.Button.Left)
    {
        if (!IsOpen)
            return false;

        _imguiBackend?.MouseButton(button, false);
        if (button == Mouse.Button.Middle)
        {
            _panningProjection = -1;
            return true;
        }

        if (button != Mouse.Button.Left)
            return true;

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

        _imguiBackend?.MousePosition(position.X, position.Y);
        _imguiBackend?.MouseWheel(0f, delta);
        if (IsImGuiUiPoint(position))
            return true;

        for (int projection = 0; projection < _previewPanels.Length; projection++)
        {
            if (!_previewPanels[projection].GetGlobalBounds().Contains(position))
                continue;

            _activeProjection = projection;
            bool controlPressed =
                Keyboard.IsKeyPressed(Keyboard.Key.LControl) ||
                Keyboard.IsKeyPressed(Keyboard.Key.RControl);
            bool shiftPressed =
                Keyboard.IsKeyPressed(Keyboard.Key.LShift) ||
                Keyboard.IsKeyPressed(Keyboard.Key.RShift);

            if (controlPressed)
            {
                float factor = delta > 0f ? 1.15f : 1f / 1.15f;
                _projectionZoom[projection] = Math.Clamp(
                    _projectionZoom[projection] * factor, 0.15f, 5f);
                _status = $"Масштаб вида «{ProjectionName(projection)}»: {_projectionZoom[projection]:0.00}×. Home — сброс.";
            }
            else if (shiftPressed)
            {
                _projectionPanX[projection] += delta > 0f ? 48f : -48f;
            }
            else
            {
                // Like a web page: wheel down moves the image upward to reveal lower body parts.
                _projectionPanY[projection] += delta > 0f ? 48f : -48f;
            }

            return true;
        }

        if (_listPanel.GetGlobalBounds().Contains(position))
        {
            int step = delta > 0f ? -3 : 3;
            _listScroll = Math.Clamp(_listScroll + step, 0, Math.Max(0, _parts.Length - _listVisibleRows));
            return true;
        }

        if (_propertiesPanel.GetGlobalBounds().Contains(position))
        {
            float scrollStep = MathF.Max(36f, _propertyRowHeight * 1.5f);
            _propertiesScrollOffset = Math.Clamp(
                _propertiesScrollOffset + (delta > 0f ? -scrollStep : scrollStep),
                0f,
                _propertiesMaxScroll);
        }

        return true;
    }

    public bool HandleTextEntered(string text)
    {
        if (!IsOpen)
            return false;

        _imguiBackend?.TextEntered(text);
        if (_imguiBackend?.WantCaptureKeyboard == true)
            return true;
        if (!_editingPartName)
            return false;

        if (string.IsNullOrEmpty(text))
            return true;

        foreach (char character in text)
        {
            if (char.IsControl(character) || _partNameEditBuffer.Length >= 64)
                continue;

            _partNameEditBuffer += character;
        }

        return true;
    }

    private void CommitPartName()
    {
        string name = _partNameEditBuffer.Trim();
        _editingPartName = false;
        _partNameEditBuffer = string.Empty;
        if (_selectedPart < 0 || _selectedPart >= _parts.Length)
            return;

        _parts[_selectedPart] = Copy(_parts[_selectedPart], displayName: name);
        _status = name.Length > 0
            ? $"Имя части изменено на «{name}». Ctrl+S — сохранить."
            : $"Для «{_regionIds[_selectedPart]}» восстановлено имя по умолчанию. Ctrl+S — сохранить.";
    }

    public void HandleKeyReleased(Keyboard.Key key)
    {
        if (IsOpen)
            _imguiBackend?.KeyEvent(key, false);
    }

    public bool HandleKey(Keyboard.Key key, bool controlPressed)
    {
        if (!IsOpen)
            return false;

        _imguiBackend?.KeyEvent(key, true);
        if (_editingPartName)
        {
            if (key == Keyboard.Key.Enter)
            {
                CommitPartName();
                return true;
            }

            if (key == Keyboard.Key.Escape)
            {
                _editingPartName = false;
                _partNameEditBuffer = string.Empty;
                _status = "Переименование отменено.";
                return true;
            }

            if (key == Keyboard.Key.Backspace && _partNameEditBuffer.Length > 0)
            {
                int removeLength = _partNameEditBuffer.Length > 1 &&
                    char.IsLowSurrogate(_partNameEditBuffer[^1]) &&
                    char.IsHighSurrogate(_partNameEditBuffer[^2]) ? 2 : 1;
                _partNameEditBuffer = _partNameEditBuffer[..^removeLength];
            }

            return true;
        }

        if (_editingProperty >= 0)
        {
            HandleNumericEditKey(key);
            return true;
        }

        if (key == Keyboard.Key.Escape && (_parentSelectionActive || _symmetryToolActive))
        {
            _parentSelectionActive = false;
            _parentSourcePart = -1;
            _symmetryToolActive = false;
            _symmetrySourcePart = -1;
            _status = "Инструмент привязки/симметрии отменён.";
            return true;
        }

        if (_imguiBackend?.WantCaptureKeyboard == true)
        {
            if (controlPressed && key == Keyboard.Key.S)
                Save();
            return true;
        }

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

        if (key == Keyboard.Key.Home)
        {
            if (_activeProjection >= 0)
            {
                _projectionZoom[_activeProjection] = 1f;
                _projectionPanX[_activeProjection] = 0f;
                _projectionPanY[_activeProjection] = 0f;
            }
            else
            {
                Array.Fill(_projectionZoom, 1f);
                Array.Clear(_projectionPanX);
                Array.Clear(_projectionPanY);
            }

            _status = "Вид сброшен. Ctrl+колесо — масштаб, колесо — вертикальная прокрутка.";
            return true;
        }

        if (key is Keyboard.Key.PageUp or Keyboard.Key.PageDown)
        {
            float pageStep = MathF.Max(80f, _propertiesViewportBottom - _propertiesViewportTop - 36f);
            _propertiesScrollOffset = Math.Clamp(
                _propertiesScrollOffset + (key == Keyboard.Key.PageDown ? pageStep : -pageStep),
                0f,
                _propertiesMaxScroll);
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

        _imguiBackend ??= new ImGuiSfmlBackend();
        Layout(width, height);
        CalculateWorldTransforms();
        if (_normalizeViewsEnabled)
            _normalizedPixelsPerUnit = CalculateNormalizedProjectionScale();

        _backdrop.Position = new Vector2f(0f, 0f);
        _backdrop.Size = new Vector2f(width, height);
        window.Draw(_backdrop);
        window.Draw(_panel);
        for (int projection = 0; projection < _previewPanels.Length; projection++)
            DrawProjection(window, projection);

        _imguiBackend.BeginFrame(width, height, 1f / 60f);
        DrawImGuiToolbar();
        DrawImGuiPartList();
        DrawImGuiProperties();
        DrawImGuiProjectionHeaders();
        _imguiBackend.Render(window);
    }

    private unsafe void DrawImGuiToolbar()
    {
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse;
        BeginImGuiWindow("##BodyRigToolbar", _header.Position, _header.Size, flags);
        TextImGui("КОНСТРУКТОР ТЕЛА КОЛОНИСТА");
        ImguiNative.igSameLine(265f, 6f);
        if (ButtonImGui("Загрузить")) LoadRigFromDialog();
        ImguiNative.igSameLine(0f, 5f);
        if (ButtonImGui("Экспорт")) ExportRigFromDialog();
        ImguiNative.igSameLine(0f, 5f);
        if (ButtonImGui("Сохранить")) Save();
        ImguiNative.igSameLine(0f, 5f);
        if (ButtonImGui(_showUnitSettings ? "К геометрии" : "Настройки юнита"))
        {
            _showUnitSettings = !_showUnitSettings;
            if (_showUnitSettings)
                _showOrganSettings = _showTissueSettings = _showEquipmentSettings = false;
        }
        ImguiNative.igSameLine(0f, 5f);
        if (ButtonImGui(_showOrganSettings ? "К геометрии" : "Органы"))
        {
            _showOrganSettings = !_showOrganSettings;
            if (_showOrganSettings)
                _showUnitSettings = _showTissueSettings = _showEquipmentSettings = false;
        }
        ImguiNative.igSameLine(0f, 5f);
        if (ButtonImGui(_showTissueSettings ? "К геометрии" : "Ткани / маркеры"))
        {
            _showTissueSettings = !_showTissueSettings;
            if (_showTissueSettings)
                _showUnitSettings = _showOrganSettings = _showEquipmentSettings = false;
        }
        ImguiNative.igSameLine(0f, 5f);
        if (ButtonImGui(_showEquipmentSettings ? "К геометрии" : "Снаряжение"))
        {
            _showEquipmentSettings = !_showEquipmentSettings;
            if (_showEquipmentSettings)
                _showUnitSettings = _showOrganSettings = _showTissueSettings = false;
        }
        ImguiNative.igSameLine(0f, 5f);
        if (ButtonImGui("По умолчанию"))
        {
            if (_defaultParts.Length == _parts.Length)
                Array.Copy(_defaultParts, _parts, _parts.Length);
            _definition = CreateDefaultColonistDefinition();
            RefreshProjectedGeometry();
            _status = "Восстановлены базовые формы. Нажми «Сохранить», чтобы применить.";
        }
        ImguiNative.igSameLine(0f, 5f);
        if (ButtonImGui("Откатить"))
        {
            SetRigData(_openedRegionIds, _openedParts, setDefaults: false, setOpenedSnapshot: false);
            _status = "Возвращена конфигурация, с которой редактор был открыт.";
        }
        ImguiNative.igSameLine(0f, 5f);
        if (ButtonImGui("Закрыть")) Close();
        ImguiNative.igEnd();
    }

    private unsafe void DrawImGuiPartList()
    {
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoSavedSettings;
        BeginImGuiWindow("##BodyRigPartList", _listPanel.Position, _listPanel.Size, flags);
        TextImGui("ЧАСТИ ТЕЛА");
        ImguiNative.igSeparator();
        ImGuiVector2 childSize = new(0f, MathF.Max(80f, _listPanel.Size.Y - 210f));
        if (ImguiNative.igBeginChild_Str("##BodyPartsScroll", childSize, default, ImGuiWindowFlags.None))
        {
            for (int i = 0; i < _parts.Length; i++)
            {
                string pairMark = GetSymmetryCounterpartIndex(i) >= 0 ? "   <->" : string.Empty;
                string sourceMark = _symmetryToolActive && i == _symmetrySourcePart ? "   [A]" : string.Empty;
                string childMark = _parentSelectionActive && i == _parentSourcePart ? "   [ДОЧЕРНЯЯ]" : string.Empty;
                string label = $"{i + 1:00}  {PartName(_regionIds[i])}{pairMark}{sourceMark}{childMark}##part{i}";
                if (ImguiNative.igSelectable_Bool(
                    label, i == _selectedPart, ImGuiSelectableFlags.None, new ImGuiVector2(0f, 24f)))
                    SelectPartFromImGui(i);
            }
        }
        ImguiNative.igEndChild();

        if (ButtonImGui("Добавить часть", _listPanel.Size.X - 28f)) AddRegion();
        if (ButtonImGui("Копировать часть", _listPanel.Size.X - 28f)) CopySelectedPart();
        if (ButtonImGui("Добавить примеры форм", _listPanel.Size.X - 28f)) AddAllPrimitiveExamples();
        if (ButtonImGui("Удалить свою часть", _listPanel.Size.X - 28f)) RemoveSelectedCustomRegion();
        string symmetryLabel = _symmetryToolActive ? "Выбери парную часть…" : $"Симметрия: {CountSymmetryPairs()} пар";
        if (ButtonImGui(symmetryLabel, _listPanel.Size.X - 28f))
        {
            _parentSelectionActive = false;
            _parentSourcePart = -1;
            _symmetryToolActive = !_symmetryToolActive;
            _symmetrySourcePart = _symmetryToolActive ? _selectedPart : -1;
            _status = _symmetryToolActive
                ? $"Исходная часть: «{PartName(_regionIds[_symmetrySourcePart])}». Теперь выбери парную часть."
                : "Инструмент симметрии закрыт.";
        }
        if (ButtonImGui("Отвязать все части", _listPanel.Size.X - 28f))
        {
            bool changed = DetachAllParentsPreservingLayout();
            _defaultParts = (BodyPartDefinition[])_parts.Clone();
            _openedRegionIds = (string[])_regionIds.Clone();
            _openedParts = (BodyPartDefinition[])_parts.Clone();
            if (changed) Save();
            _status = changed
                ? "Все родительские связи удалены, расположение частей сохранено."
                : "Все части уже независимы.";
        }
        ImguiNative.igEnd();
    }

    private unsafe void DrawImGuiProperties()
    {
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoSavedSettings;
        BeginImGuiWindow("##BodyRigProperties", _propertiesPanel.Position, _propertiesPanel.Size, flags);
        if (_showUnitSettings)
        {
            DrawImGuiUnitSettings();
            ImguiNative.igEnd();
            return;
        }
        if (_showOrganSettings)
        {
            DrawImGuiOrganSettings();
            ImguiNative.igEnd();
            return;
        }
        if (_showTissueSettings)
        {
            DrawImGuiTissueSettings();
            ImguiNative.igEnd();
            return;
        }
        if (_showEquipmentSettings)
        {
            DrawImGuiEquipmentSettings();
            ImguiNative.igEnd();
            return;
        }

        BodyPartDefinition part = _parts[_selectedPart];
        TextImGui("ПАРАМЕТРЫ ЧАСТИ");
        ImguiNative.igSeparator();
        TextImGui(PartName(_regionIds[_selectedPart]));
        TextImGui($"Внутренний ID: {_regionIds[_selectedPart]}");
        string parent = part.ParentIndex >= 0 ? PartName(_regionIds[part.ParentIndex]) : "Корень юнита";
        TextImGui($"Родитель: {parent}");

        if (ButtonImGui(_parentSelectionActive ? "Выбери родителя слева…" : "Назначить родителя"))
        {
            _symmetryToolActive = false;
            _symmetrySourcePart = -1;
            _parentSelectionActive = !_parentSelectionActive;
            _parentSourcePart = _parentSelectionActive ? _selectedPart : -1;
            _status = _parentSelectionActive
                ? $"Назначь родителя для «{PartName(_regionIds[_parentSourcePart])}»: выбери часть выше неё слева."
                : "Выбор родителя отменён.";
        }
        ImguiNative.igSameLine(0f, 5f);
        if (ButtonImGui("Отвязать"))
        {
            bool changed = DetachPartPreservingLayout(_selectedPart);
            if (changed) Save();
            _status = changed
                ? $"«{PartName(_regionIds[_selectedPart])}» отвязана с сохранением положения."
                : "Эта часть уже независима.";
            RefreshProjectedGeometry();
        }

        TextImGui("Имя части (пусто = исходное)");
        DrawImGuiPartNameInput();
        ImguiNative.igSeparatorText("Положение, размер и углы");
        string[] propertyNames =
        {
            "Локальный X", "Локальный Y", "Локальный Z",
            "Размер X", "Размер Y", "Размер Z",
            "Наклон X (°)", "Наклон Y (°)", "Поворот Z (°)"
        };
        for (int i = 0; i < PropertyCount; i++)
        {
            PropertyInfo property = GetProperty(i);
            TextImGui(propertyNames[i]);
            float sliderValue = property.Value;
            ImguiNative.igSetNextItemWidth(116f);
            if (ImguiNative.igSliderFloat(
                $"##propSlider{i}", &sliderValue, property.Minimum, property.Maximum, "%.3f", ImGuiSliderFlags.None))
                SetProperty(i, sliderValue);
            ImguiNative.igSameLine(0f, 5f);
            float inputValue = GetProperty(i).Value;
            ImguiNative.igSetNextItemWidth(78f);
            if (ImguiNative.igInputFloat(
                $"##propInput{i}", &inputValue, 0f, 0f, "%.4f", ImGuiInputTextFlags.None))
                SetProperty(i, inputValue);
        }

        if (ImguiNative.igBeginCombo("Форма", _parts[_selectedPart].Primitive.ToString(), ImGuiComboFlags.None))
        {
            foreach (BodyPrimitive primitive in Enum.GetValues<BodyPrimitive>())
            {
                bool selected = _parts[_selectedPart].Primitive == primitive;
                if (ImguiNative.igSelectable_Bool(
                    primitive.ToString(), selected, ImGuiSelectableFlags.None, new ImGuiVector2(0f, 0f)))
                {
                    _parts[_selectedPart] = Copy(_parts[_selectedPart], primitive: primitive);
                    ApplySizeSymmetry();
                    RefreshProjectedGeometry();
                }
            }
            ImguiNative.igEndCombo();
        }

        DrawImGuiEnumCombo("Режим движения", part.Motion, value =>
            _parts[_selectedPart] = Copy(_parts[_selectedPart], motion: value));
        DrawImGuiFloatInput("Дистанция следования", "##followDistance", part.FollowDistance,
            value => _parts[_selectedPart] = Copy(_parts[_selectedPart], followDistance: value),
            0f, 100f);
        DrawImGuiEnumCombo("Точка привязки", part.Anchor, value =>
            _parts[_selectedPart] = Copy(_parts[_selectedPart], anchor: value));
        DrawImGuiEnumCombo("Роль части", part.Role, value =>
            _parts[_selectedPart] = Copy(_parts[_selectedPart], role: value));
        DrawImGuiEnumCombo("Pivot", part.Pivot, value =>
            _parts[_selectedPart] = Copy(_parts[_selectedPart], pivot: value));

        ImguiNative.igSeparatorText("Симметрия и инструменты");
        if (ButtonImGui("Выбрать симметричную пару"))
        {
            _parentSelectionActive = false;
            _parentSourcePart = -1;
            _symmetryToolActive = true;
            _symmetrySourcePart = _selectedPart;
            _status = $"Исходная часть: «{PartName(_regionIds[_selectedPart])}». Теперь выбери парную часть слева.";
        }
        if (ButtonImGui("Снять симметрию у пары"))
        {
            ClearSymmetryPair(_selectedPart);
            RefreshProjectedGeometry();
            _status = $"Симметрия для «{PartName(_regionIds[_selectedPart])}» снята.";
        }

        byte grid = (byte)(_gridEnabled ? 1 : 0);
        if (ImguiNative.igCheckbox("Сетка и привязка", &grid))
        {
            _gridEnabled = grid != 0;
            _status = _gridEnabled ? "Сетка и привязка включены." : "Сетка и привязка выключены.";
        }
        if (ButtonImGui($"Шаг сетки: {_gridSteps[_gridStepIndex]:0.####}"))
            _gridStepIndex = (_gridStepIndex + 1) % _gridSteps.Length;

        byte surfaceSnap = (byte)(_surfaceSnapEnabled ? 1 : 0);
        if (ImguiNative.igCheckbox("Привязка к нормали", &surfaceSnap))
        {
            _surfaceSnapEnabled = surfaceSnap != 0;
            _surfaceSnapTargetPart = -1;
            _surfaceSnapProjection = -1;
            _hasSurfaceSnapDirection = false;
        }
        byte normalized = (byte)(_normalizeViewsEnabled ? 1 : 0);
        if (ImguiNative.igCheckbox("Общий масштаб трёх видов", &normalized))
            _normalizeViewsEnabled = normalized != 0;
        if (ButtonImGui("Центрировать часть (0,0,0)", _propertiesPanel.Size.X - 28f))
            ResetSelectedPartToBodyCenter();

        byte allSymmetry = (byte)(_symmetryEnabled[0] && _symmetryEnabled[1] && _symmetryEnabled[2] ? 1 : 0);
        if (ImguiNative.igCheckbox("Зеркальное перемещение во всех видах", &allSymmetry))
            Array.Fill(_symmetryEnabled, allSymmetry != 0);
        ImguiNative.igSeparator();
        TextImGui(_status);
        ImguiNative.igEnd();
    }

    private unsafe void DrawImGuiUnitSettings()
    {
        TextImGui("НАСТРОЙКИ И ФАЙЛ ПРОФИЛЯ ЮНИТА");
        ImguiNative.igSeparator();

        if (ButtonImGui("Новый юнит"))
            CreateNewProjectUnit();
        ImguiNative.igSameLine(0f, 5f);
        if (ButtonImGui("Открыть файл"))
            OpenProjectUnitFileDialog();
        ImguiNative.igSameLine(0f, 5f);
        if (ButtonImGui("Сохранить файл"))
            SaveProjectUnitFile(saveAs: false);
        ImguiNative.igSameLine(0f, 5f);
        if (ButtonImGui("Сохранить как..."))
            SaveProjectUnitFile(saveAs: true);

        if (_editingProjectAsset)
        {
            TextImGui($"Пользовательский архетип · runtimeTypeId: {_projectRuntimeTypeId}");
            if (string.IsNullOrWhiteSpace(_projectUnitPath))
            {
                _projectUnitId = DrawImGuiTextInput(
                    "ID файла (стабильный)", "##projectUnitId", _projectUnitId,
                    _imguiAssetIdBuffer, ref _imguiAssetIdValue);
            }
            else
            {
                TextImGui($"ID: {_projectUnitId}  ·  переименование через «Сохранить как...»");
            }

            _projectUnitDisplayName = DrawImGuiTextInput(
                "Отображаемое имя", "##projectUnitName", _projectUnitDisplayName,
                _imguiAssetNameBuffer, ref _imguiAssetNameValue);
            _projectUnitDescription = DrawImGuiTextInput(
                "Описание", "##projectUnitDescription", _projectUnitDescription,
                _imguiAssetDescriptionBuffer, ref _imguiAssetDescriptionValue);

            TextImGui(string.IsNullOrWhiteSpace(_projectUnitPath)
                ? $"Новый файл будет сохранён в {UnitArchetypeFileStore.ProjectDirectory}"
                : $"Файл: {_projectUnitPath}");
        }
        else
        {
            TextImGui("Сейчас открыт встроенный профиль «Колонист». «Новый юнит» создаёт отдельный файл, не перезаписывая колониста.");
        }

        ImguiNative.igSeparatorText("Характеристики");
        TextImGui($"Активный профиль: {(_editingProjectAsset ? _projectUnitDisplayName : UnitAnatomyCatalog.GetDisplayName(_definition.Type))}");
        DrawImGuiEnumCombo("Тип тела", _definition.BodyType,
            value => UpdateDefinition(bodyType: value));

        DrawImGuiFloatInput("Скорость перемещения", "##unitMoveSpeed", _definition.MoveSpeed,
            value => UpdateDefinition(moveSpeed: value), 0.05f, 100f);
        DrawImGuiFloatInput("Ширина", "##unitWidth", _definition.Width,
            value => UpdateDefinition(width: value), 0.01f, 100f);
        DrawImGuiFloatInput("Длина", "##unitLength", _definition.Length,
            value => UpdateDefinition(length: value), 0.01f, 100f);
        DrawImGuiFloatInput("Высота", "##unitHeight", _definition.Height,
            value => UpdateDefinition(height: value), 0.01f, 100f);
        DrawImGuiFloatInput("Дальность обзора", "##unitViewRange", _definition.ViewRange,
            value => UpdateDefinition(viewRange: value), 0.1f, 1000f);
        DrawImGuiFloatInput("Поле зрения (градусы)", "##unitFieldOfView", _definition.FieldOfView,
            value => UpdateDefinition(fieldOfView: value), 1f, 360f);

        ImguiNative.igSeparator();
        TextImGui(_editingProjectAsset
            ? "Сохранение создаёт один .unit.json с геометрией, тканями, органами, маркерами и экипировкой."
            : "«Сохранить» применяет изменения к встроенному профилю. Для отдельного существа нажми «Новый юнит».");
    }

    private unsafe void DrawImGuiOrganSettings()
    {
        TextImGui("ОРГАНЫ И ВНУТРЕННИЕ ОБЪЁМЫ");
        ImguiNative.igSeparator();
        TextImGui("Геометрия и влияние внутреннего объёма на попадание, пробитие, урон и кровотечение.");

        if (ButtonImGui("Добавить орган"))
            AddOrganVolume();
        ImguiNative.igSameLine(0f, 6f);
        if (ButtonImGui("Удалить выбранный") && _organs.Length > 0)
            RemoveOrganVolume();

        if (_organs.Length == 0)
        {
            TextImGui("Органов нет. Добавь внутренний объём.");
            TextImGui(_status);
            return;
        }

        _selectedOrgan = Math.Clamp(_selectedOrgan, 0, _organs.Length - 1);
        UnitAnatomyVolume organ = _organs[_selectedOrgan];
        DrawImGuiStringCombo(
            "Орган",
            organ.Id,
            Array.ConvertAll(_organs, item => item.Id),
            id =>
            {
                int index = Array.FindIndex(_organs, item => string.Equals(item.Id, id, StringComparison.Ordinal));
                if (index >= 0)
                    _selectedOrgan = index;
            });

        DrawImGuiStringCombo(
            "Область размещения",
            organ.ParentRegionId,
            _regionIds,
            regionId => ChangeOrganParent(regionId));

        organ = _organs[_selectedOrgan];
        DrawImGuiStringCombo(
            "Входная ткань",
            organ.EntryBindingId,
            GetTissueIdsForRegion(organ.ParentRegionId),
            id => UpdateSelectedOrgan(value => CopyVolume(value, entryBindingId: id)));
        DrawImGuiStringCombo(
            "Ткань-цель",
            organ.TargetBindingId,
            GetAllTissueIds(),
            id => UpdateSelectedOrgan(value => CopyVolume(value, targetBindingId: id)));

        organ = _organs[_selectedOrgan];
        DrawImGuiEnumCombo("Форма объёма", organ.Primitive,
            value => UpdateSelectedOrgan(current => CopyVolume(current, primitive: value)));
        DrawImGuiFloatInput("Центр X", "##organCenterX", organ.LocalCenter.X,
            value => UpdateSelectedOrgan(current => CopyVolume(current, center: new NumericsVector3(value, current.LocalCenter.Y, current.LocalCenter.Z))), -3f, 3f);
        DrawImGuiFloatInput("Центр Y", "##organCenterY", organ.LocalCenter.Y,
            value => UpdateSelectedOrgan(current => CopyVolume(current, center: new NumericsVector3(current.LocalCenter.X, value, current.LocalCenter.Z))), -3f, 3f);
        DrawImGuiFloatInput("Центр Z", "##organCenterZ", organ.LocalCenter.Z,
            value => UpdateSelectedOrgan(current => CopyVolume(current, center: new NumericsVector3(current.LocalCenter.X, current.LocalCenter.Y, value))), -3f, 3f);
        DrawImGuiFloatInput("Полуразмер X", "##organHalfX", organ.HalfExtents.X,
            value => UpdateSelectedOrgan(current => CopyVolume(current, halfExtents: new NumericsVector3(value, current.HalfExtents.Y, current.HalfExtents.Z))), 0.001f, 2f);
        DrawImGuiFloatInput("Полуразмер Y", "##organHalfY", organ.HalfExtents.Y,
            value => UpdateSelectedOrgan(current => CopyVolume(current, halfExtents: new NumericsVector3(current.HalfExtents.X, value, current.HalfExtents.Z))), 0.001f, 2f);
        DrawImGuiFloatInput("Полуразмер Z", "##organHalfZ", organ.HalfExtents.Z,
            value => UpdateSelectedOrgan(current => CopyVolume(current, halfExtents: new NumericsVector3(current.HalfExtents.X, current.HalfExtents.Y, value))), 0.001f, 2f);
        DrawImGuiFloatInput("Поворот (градусы)", "##organRotation", organ.LocalRotation * 180f / MathF.PI,
            value => UpdateSelectedOrgan(current => CopyVolume(current, rotation: value * MathF.PI / 180f)), -180f, 180f);

        ImguiNative.igSeparatorText("Воздействие на снаряд и урон");
        DrawImGuiFloatInput("Потеря энергии", "##organEnergyLoss", organ.EnergyLoss,
            value => UpdateSelectedOrgan(current => CopyVolume(current, energyLoss: value)), 0f, 200f);
        DrawImGuiFloatInput("Потеря пробития", "##organPenLoss", organ.PenetrationLoss,
            value => UpdateSelectedOrgan(current => CopyVolume(current, penetrationLoss: value)), 0f, 30f);
        DrawImGuiFloatInput("Множитель урона", "##organDamageMultiplier", organ.DamageMultiplier,
            value => UpdateSelectedOrgan(current => CopyVolume(current, damageMultiplier: value)), 0f, 5f);
        DrawImGuiFloatInput("Множитель кровотечения", "##organBleedMultiplier", organ.BleedMultiplier,
            value => UpdateSelectedOrgan(current => CopyVolume(current, bleedMultiplier: value)), 0f, 5f);
        TextImGui("Органы сохраняются вместе с профилем кнопкой «Сохранить».");
        TextImGui(_status);
    }

    private unsafe void DrawImGuiTissueSettings()
    {
        if (_parts.Length == 0)
        {
            TextImGui("Нет областей тела.");
            return;
        }

        _selectedPart = Math.Clamp(_selectedPart, 0, _parts.Length - 1);
        TextImGui("ПОВРЕЖДАЕМАЯ АНАТОМИЯ И ФУНКЦИОНАЛЬНЫЕ МАРКЕРЫ");
        ImguiNative.igSeparator();
        DrawImGuiStringCombo(
            "Область тела",
            _regionIds[_selectedPart],
            _regionIds,
            id =>
            {
                int index = Array.IndexOf(_regionIds, id);
                if (index >= 0)
                {
                    _selectedPart = index;
                    _selectedTissue = 0;
                    _selectedMarker = 0;
                }
            });
        TextImGui($"Область: {PartName(_regionIds[_selectedPart])} ({_regionIds[_selectedPart]})");
        DrawImGuiFloatInput("Полутолщина области попадания", $"##hitHalfHeight{_selectedPart}",
            _hitHalfHeights[_selectedPart],
            value => _hitHalfHeights[_selectedPart] = value, 0.001f, 2f);

        HitRegionPartBinding[] tissues = _tissuesByRegion[_selectedPart];
        if (ButtonImGui("Добавить ткань"))
            AddTissueBinding();
        ImguiNative.igSameLine(0f, 6f);
        if (ButtonImGui("Удалить ткань") && tissues.Length > 1)
            RemoveTissueBinding();

        tissues = _tissuesByRegion[_selectedPart];
        if (tissues.Length == 0)
        {
            TextImGui("В области должна быть хотя бы одна ткань.");
            return;
        }

        _selectedTissue = Math.Clamp(_selectedTissue, 0, tissues.Length - 1);
        HitRegionPartBinding tissue = tissues[_selectedTissue];
        DrawImGuiStringCombo(
            "Ткань",
            tissue.Id,
            Array.ConvertAll(tissues, item => item.Id),
            id =>
            {
                int index = Array.FindIndex(_tissuesByRegion[_selectedPart],
                    item => string.Equals(item.Id, id, StringComparison.Ordinal));
                if (index >= 0)
                    _selectedTissue = index;
            });

        tissue = _tissuesByRegion[_selectedPart][_selectedTissue];
        TextImGui($"ID: {tissue.Id}   Родительский ID: {tissue.ParentId}");
        DrawImGuiEnumCombo("Игровая часть", tissue.Part,
            value => UpdateSelectedTissue(current => CopyTissue(current, part: value)));
        DrawImGuiEnumCombo("Тип ткани", tissue.Kind,
            value => UpdateSelectedTissue(current => CopyTissue(current, kind: value)));
        DrawImGuiEnumCombo("Родительская часть (legacy)", tissue.ParentPart,
            value => UpdateSelectedTissue(current => CopyTissue(current, parentPart: value)));
        DrawImGuiFloatInput("Вес распределения урона", "##tissueWeight", tissue.Weight,
            value => UpdateSelectedTissue(current => CopyTissue(current, weight: value)), 0.001f, 1000f);
        DrawImGuiFloatInput("Максимум здоровья (HP)", "##tissueHitPoints", tissue.MaxHitPoints,
            value => UpdateSelectedTissue(current => CopyTissue(current, maxHitPoints: value)), 0.01f, 100000f);
        DrawImGuiFloatInput("Бронезона (-1 = нет)", "##tissueArmorZone", tissue.ArmorZone,
            value => UpdateSelectedTissue(current => CopyTissue(current, armorZone: (int)MathF.Round(value))),
            -1f, UnitInventoryStore.BodyZoneCount - 1);
        DrawImGuiFloatInput("Потеря энергии снаряда (-1 = наследовать)", "##tissueEnergyLoss",
            tissue.ProjectileEnergyLoss,
            value => UpdateSelectedTissue(current => CopyTissue(current, projectileEnergyLoss: value)), -1f, 10000f);
        DrawImGuiFloatInput("Потеря пробития (-1 = наследовать)", "##tissuePenLoss",
            tissue.ProjectilePenetrationLoss,
            value => UpdateSelectedTissue(current => CopyTissue(current, projectilePenetrationLoss: value)), -1f, 1000f);

        ImguiNative.igSeparatorText("Маркеры обнаружения, прицеливания и взаимодействия");
        UnitAnatomyMarker[] markers = _markersByRegion[_selectedPart];
        if (ButtonImGui("Добавить маркер"))
            AddAnatomyMarker();
        ImguiNative.igSameLine(0f, 6f);
        if (ButtonImGui("Удалить маркер") && markers.Length > 0)
            RemoveAnatomyMarker();

        markers = _markersByRegion[_selectedPart];
        if (markers.Length > 0)
        {
            _selectedMarker = Math.Clamp(_selectedMarker, 0, markers.Length - 1);
            UnitAnatomyMarker anatomyMarker = markers[_selectedMarker];
            DrawImGuiStringCombo(
                "Маркер",
                anatomyMarker.Id,
                Array.ConvertAll(markers, item => item.Id),
                id =>
                {
                    int index = Array.FindIndex(_markersByRegion[_selectedPart],
                        item => string.Equals(item.Id, id, StringComparison.Ordinal));
                    if (index >= 0)
                        _selectedMarker = index;
                });

            anatomyMarker = _markersByRegion[_selectedPart][_selectedMarker];
            DrawImGuiFloatInput("Смещение маркера X", "##markerX", anatomyMarker.LocalOffset.X,
                value => UpdateSelectedMarker(current => CopyMarker(current,
                    offset: new NumericsVector3(value, current.LocalOffset.Y, current.LocalOffset.Z))), -3f, 3f);
            DrawImGuiFloatInput("Смещение маркера Y", "##markerY", anatomyMarker.LocalOffset.Y,
                value => UpdateSelectedMarker(current => CopyMarker(current,
                    offset: new NumericsVector3(current.LocalOffset.X, value, current.LocalOffset.Z))), -3f, 3f);
            DrawImGuiFloatInput("Смещение маркера Z", "##markerZ", anatomyMarker.LocalOffset.Z,
                value => UpdateSelectedMarker(current => CopyMarker(current,
                    offset: new NumericsVector3(current.LocalOffset.X, current.LocalOffset.Y, value))), -3f, 3f);
            DrawImGuiMarkerRoleCheckbox("Зрение", UnitAnatomyMarkerRole.Vision);
            DrawImGuiMarkerRoleCheckbox("Цель для попаданий", UnitAnatomyMarkerRole.Target);
            DrawImGuiMarkerRoleCheckbox("Прицеливание", UnitAnatomyMarkerRole.Aim);
            DrawImGuiMarkerRoleCheckbox("Глаз", UnitAnatomyMarkerRole.Eye);
            DrawImGuiMarkerRoleCheckbox("Жизненно важная точка", UnitAnatomyMarkerRole.Vital);
            DrawImGuiMarkerRoleCheckbox("Взаимодействие", UnitAnatomyMarkerRole.Interaction);
            DrawImGuiMarkerRoleCheckbox("Передвижение", UnitAnatomyMarkerRole.Locomotion);
            DrawImGuiMarkerRoleCheckbox("Крепление оружия", UnitAnatomyMarkerRole.WeaponMount);
            DrawImGuiMarkerRoleCheckbox("Дуло", UnitAnatomyMarkerRole.Muzzle);
        }

        TextImGui("Выбери область тела слева; здесь настраиваются попадания, прочность и функциональные точки.");
        TextImGui(_status);
    }

    private unsafe void DrawImGuiEquipmentSettings()
    {
        TextImGui("СТАРТОВОЕ СНАРЯЖЕНИЕ ЮНИТА");
        ImguiNative.igSeparator();
        TextImGui("Здесь задаются предметы экипировки архетипа и их начальная прочность.");

        if (ButtonImGui("Добавить предмет"))
            AddStartingEquipment();
        ImguiNative.igSameLine(0f, 6f);
        if (ButtonImGui("Удалить выбранный") && _startingEquipment.Length > 0)
            RemoveStartingEquipment();

        if (_startingEquipment.Length == 0)
        {
            TextImGui("Стартовое снаряжение отсутствует.");
            TextImGui(_status);
            return;
        }

        _selectedEquipment = Math.Clamp(_selectedEquipment, 0, _startingEquipment.Length - 1);
        UnitStartingEquipment equipment = _startingEquipment[_selectedEquipment];
        string[] equipmentLabels = _startingEquipment
            .Select((item, index) => $"{index + 1:00} — {GetApparelId(item.Item)} — {item.Item.Name}")
            .ToArray();
        DrawImGuiStringCombo(
            "Слот экипировки",
            equipmentLabels[_selectedEquipment],
            equipmentLabels,
            label =>
            {
                int index = Array.IndexOf(equipmentLabels, label);
                if (index >= 0)
                    _selectedEquipment = index;
            });

        equipment = _startingEquipment[_selectedEquipment];
        DrawImGuiStringCombo(
            "Предмет",
            GetApparelId(equipment.Item),
            new[]
            {
                nameof(ApparelCatalog.WorkShirt),
                nameof(ApparelCatalog.WorkTrousers),
                nameof(ApparelCatalog.CombatVest),
                nameof(ApparelCatalog.CombatHelmet),
                nameof(ApparelCatalog.PoweredExoskeleton)
            },
            id => ChangeStartingEquipmentItem(GetApparel(id)));

        equipment = _startingEquipment[_selectedEquipment];
        DrawImGuiFloatInput("Начальная прочность", "##equipmentDurability", equipment.Durability,
            value => UpdateStartingEquipment(current => new UnitStartingEquipment(current.Item, value)),
            0.01f, 10000f);

        ArmorConfig armor = equipment.Item;
        ImguiNative.igSeparatorText("Основные параметры предмета");
        DrawImGuiArmorNameInput(armor);
        DrawImGuiFloatInput("Масса", "##armorWeight", armor.Weight,
            value => UpdateSelectedArmor(current => CopyArmor(current, weight: value)), 0f, 1000f);
        DrawImGuiEnumCombo("Слой экипировки", armor.Layer,
            value => UpdateSelectedArmor(current => CopyArmor(current, layer: value)));
        DrawImGuiFloatInput("Legacy protected zone", "##armorProtectedZone", armor.ProtectedZone,
            value => UpdateSelectedArmor(current => CopyArmor(current, protectedZone: (int)MathF.Round(value))), -1f, UnitInventoryStore.BodyZoneCount - 1);
        DrawImGuiFloatInput("Поглощение урона", "##armorAbsorption", armor.DamageAbsorption,
            value => UpdateSelectedArmor(current => CopyArmor(current, damageAbsorption: value)), 0f, 1f);
        DrawImGuiFloatInput("Коэффициент повреждения брони", "##armorDamageCoefficient", armor.ArmorDamageCoefficient,
            value => UpdateSelectedArmor(current => CopyArmor(current, armorDamageCoefficient: value)), 0f, 1f);
        DrawImGuiFloatInput("Защита от кровотечения", "##armorBleedProtection", armor.BleedProtectionChance,
            value => UpdateSelectedArmor(current => CopyArmor(current, bleedProtectionChance: value)), 0f, 1f);
        DrawImGuiFloatInput("Потеря энергии снаряда", "##armorEnergyLoss", armor.EnergyLoss,
            value => UpdateSelectedArmor(current => CopyArmor(current, energyLoss: value)), 0f, 10000f);
        DrawImGuiFloatInput("Сопротивление пробитию", "##armorPenetration", armor.PenetrationResistance,
            value => UpdateSelectedArmor(current => CopyArmor(current, penetrationResistance: value)), 0f, 10000f);
        DrawImGuiFloatInput("Armor rating", "##armorRating", armor.ArmorRating,
            value => UpdateSelectedArmor(current => CopyArmor(current, armorRating: value)), 0f, 10000f);
        DrawImGuiFloatInput("Sharp rating", "##armorSharp", armor.SharpRating,
            value => UpdateSelectedArmor(current => CopyArmor(current, sharpRating: value)), 0f, 10000f);
        DrawImGuiFloatInput("Blunt rating", "##armorBlunt", armor.BluntRating,
            value => UpdateSelectedArmor(current => CopyArmor(current, bluntRating: value)), 0f, 10000f);
        DrawImGuiFloatInput("Heat rating", "##armorHeat", armor.HeatRating,
            value => UpdateSelectedArmor(current => CopyArmor(current, heatRating: value)), 0f, 10000f);
        DrawImGuiFloatInput("Hard armor damage factor", "##armorHardDamageFactor", armor.HardArmorDamageFactor,
            value => UpdateSelectedArmor(current => CopyArmor(current, hardArmorDamageFactor: value)), 0f, 1f);
        DrawImGuiFloatInput("Отступ отображения в мире", "##armorRenderPadding", armor.WorldRenderPadding,
            value => UpdateSelectedArmor(current => CopyArmor(current, worldRenderPadding: value)), 0f, 2f);
        DrawImGuiFloatInput("Множитель скорости движения", "##armorMovementSpeed", armor.MovementSpeedMultiplier,
            value => UpdateSelectedArmor(current => CopyArmor(current, movementSpeedMultiplier: value)), 0f, 5f);

        byte softArmor = (byte)(armor.SoftArmor ? 1 : 0);
        if (ImguiNative.igCheckbox("Мягкая броня", &softArmor))
        {
            bool softArmorEnabled = softArmor != 0;
            UpdateSelectedArmor(current => CopyArmor(current, softArmor: softArmorEnabled));
        }

        ImguiNative.igSeparatorText("Покрытие тела");
        DrawImGuiCoverageCheckbox("Голова", ArmorCoverage.Head);
        ImguiNative.igSameLine(0f, 8f);
        DrawImGuiCoverageCheckbox("Торс", ArmorCoverage.Torso);
        DrawImGuiCoverageCheckbox("Левая рука", ArmorCoverage.LeftArm);
        ImguiNative.igSameLine(0f, 8f);
        DrawImGuiCoverageCheckbox("Правая рука", ArmorCoverage.RightArm);
        DrawImGuiCoverageCheckbox("Левая кисть", ArmorCoverage.LeftHand);
        ImguiNative.igSameLine(0f, 8f);
        DrawImGuiCoverageCheckbox("Правая кисть", ArmorCoverage.RightHand);
        DrawImGuiCoverageCheckbox("Левая нога", ArmorCoverage.LeftLeg);
        ImguiNative.igSameLine(0f, 8f);
        DrawImGuiCoverageCheckbox("Правая нога", ArmorCoverage.RightLeg);
        DrawImGuiCoverageCheckbox("Левая стопа", ArmorCoverage.LeftFoot);
        ImguiNative.igSameLine(0f, 8f);
        DrawImGuiCoverageCheckbox("Правая стопа", ArmorCoverage.RightFoot);

        ImguiNative.igSeparatorText("Цвет предмета");
        DrawImGuiFloatInput("Красный", "##armorColorR", armor.WorldColorR,
            value => UpdateSelectedArmor(current => CopyArmor(current, worldColorR: (byte)Math.Clamp((int)MathF.Round(value), 0, 255))), 0f, 255f);
        DrawImGuiFloatInput("Зелёный", "##armorColorG", armor.WorldColorG,
            value => UpdateSelectedArmor(current => CopyArmor(current, worldColorG: (byte)Math.Clamp((int)MathF.Round(value), 0, 255))), 0f, 255f);
        DrawImGuiFloatInput("Синий", "##armorColorB", armor.WorldColorB,
            value => UpdateSelectedArmor(current => CopyArmor(current, worldColorB: (byte)Math.Clamp((int)MathF.Round(value), 0, 255))), 0f, 255f);

        TextImGui("Параметры брони, прочность, состав и цвет сохраняются кнопкой «Сохранить».");
        TextImGui(_status);
    }

    private unsafe void DrawImGuiArmorNameInput(ArmorConfig armor)
    {
        if (_imguiNameEquipmentIndex != _selectedEquipment)
        {
            Array.Fill(_imguiEquipmentNameBuffer, (byte)0);
            byte[] current = Encoding.UTF8.GetBytes(armor.Name ?? string.Empty);
            Array.Copy(current, _imguiEquipmentNameBuffer,
                Math.Min(current.Length, _imguiEquipmentNameBuffer.Length - 1));
            _imguiNameEquipmentIndex = _selectedEquipment;
        }

        TextImGui("Название предмета");
        fixed (byte* text = _imguiEquipmentNameBuffer)
        {
            ImguiNative.igSetNextItemWidth(MathF.Max(90f, _propertiesPanel.Size.X - 34f));
            if (ImguiNative.igInputText(
                "##EquipmentName", text, (uint)_imguiEquipmentNameBuffer.Length,
                ImGuiInputTextFlags.None, null, null))
            {
                int end = Array.IndexOf(_imguiEquipmentNameBuffer, (byte)0);
                if (end < 0) end = _imguiEquipmentNameBuffer.Length;
                string name = Encoding.UTF8.GetString(_imguiEquipmentNameBuffer, 0, end).Trim();
                UpdateSelectedArmor(current => CopyArmor(current, name: name));
            }
        }
    }

    private unsafe void DrawImGuiCoverageCheckbox(string label, ArmorCoverage coverage)
    {
        if (_startingEquipment.Length == 0)
            return;

        ArmorCoverage currentCoverage = _startingEquipment[_selectedEquipment].Item.Coverage;
        byte enabled = (byte)((currentCoverage & coverage) != 0 ? 1 : 0);
        if (ImguiNative.igCheckbox(label, &enabled))
        {
            ArmorCoverage next = enabled != 0
                ? currentCoverage | coverage
                : currentCoverage & ~coverage;
            UpdateSelectedArmor(current => CopyArmor(current, coverage: next));
        }
    }

    private void UpdateSelectedArmor(Func<ArmorConfig, ArmorConfig> update)
    {
        if (_startingEquipment.Length == 0 ||
            _selectedEquipment < 0 ||
            _selectedEquipment >= _startingEquipment.Length)
            return;

        UnitStartingEquipment current = _startingEquipment[_selectedEquipment];
        ArmorConfig updated = update(current.Item);
        _startingEquipment[_selectedEquipment] =
            new UnitStartingEquipment(updated, current.Durability);
        _imguiNameEquipmentIndex = -1;
    }

    private static ArmorConfig CopyArmor(
        ArmorConfig original,
        string? name = null,
        float? weight = null,
        byte? worldColorR = null,
        byte? worldColorG = null,
        byte? worldColorB = null,
        int? protectedZone = null,
        float? damageAbsorption = null,
        float? armorDamageCoefficient = null,
        float? bleedProtectionChance = null,
        float? energyLoss = null,
        float? penetrationResistance = null,
        float? sharpRating = null,
        float? bluntRating = null,
        float? heatRating = null,
        bool? softArmor = null,
        float? hardArmorDamageFactor = null,
        float? armorRating = null,
        ArmorCoverage? coverage = null,
        ArmorLayer? layer = null,
        float? worldRenderPadding = null,
        float? movementSpeedMultiplier = null) =>
        new()
        {
            Name = name ?? original.Name,
            Weight = weight ?? original.Weight,
            WorldColorR = worldColorR ?? original.WorldColorR,
            WorldColorG = worldColorG ?? original.WorldColorG,
            WorldColorB = worldColorB ?? original.WorldColorB,
            ProtectedZone = protectedZone ?? original.ProtectedZone,
            DamageAbsorption = damageAbsorption ?? original.DamageAbsorption,
            ArmorDamageCoefficient = armorDamageCoefficient ?? original.ArmorDamageCoefficient,
            BleedProtectionChance = bleedProtectionChance ?? original.BleedProtectionChance,
            EnergyLoss = energyLoss ?? original.EnergyLoss,
            PenetrationResistance = penetrationResistance ?? original.PenetrationResistance,
            SharpRating = sharpRating ?? original.SharpRating,
            BluntRating = bluntRating ?? original.BluntRating,
            HeatRating = heatRating ?? original.HeatRating,
            SoftArmor = softArmor ?? original.SoftArmor,
            HardArmorDamageFactor = hardArmorDamageFactor ?? original.HardArmorDamageFactor,
            ArmorRating = armorRating ?? original.ArmorRating,
            Coverage = coverage ?? original.Coverage,
            Layer = layer ?? original.Layer,
            WorldRenderPadding = worldRenderPadding ?? original.WorldRenderPadding,
            MovementSpeedMultiplier = movementSpeedMultiplier ?? original.MovementSpeedMultiplier
        };

    private static string GetApparelId(ArmorConfig item)
    {
        if (ReferenceEquals(item, ApparelCatalog.WorkShirt)) return nameof(ApparelCatalog.WorkShirt);
        if (ReferenceEquals(item, ApparelCatalog.WorkTrousers)) return nameof(ApparelCatalog.WorkTrousers);
        if (ReferenceEquals(item, ApparelCatalog.CombatVest)) return nameof(ApparelCatalog.CombatVest);
        if (ReferenceEquals(item, ApparelCatalog.CombatHelmet)) return nameof(ApparelCatalog.CombatHelmet);
        if (ReferenceEquals(item, ApparelCatalog.PoweredExoskeleton)) return nameof(ApparelCatalog.PoweredExoskeleton);
        return item.Name;
    }

    private static ArmorConfig GetApparel(string id) => id switch
    {
        nameof(ApparelCatalog.WorkShirt) => ApparelCatalog.WorkShirt,
        nameof(ApparelCatalog.WorkTrousers) => ApparelCatalog.WorkTrousers,
        nameof(ApparelCatalog.CombatVest) => ApparelCatalog.CombatVest,
        nameof(ApparelCatalog.CombatHelmet) => ApparelCatalog.CombatHelmet,
        nameof(ApparelCatalog.PoweredExoskeleton) => ApparelCatalog.PoweredExoskeleton,
        _ => ApparelCatalog.WorkShirt
    };

    private void AddStartingEquipment()
    {
        ArmorConfig[] options =
        {
            ApparelCatalog.WorkShirt,
            ApparelCatalog.WorkTrousers,
            ApparelCatalog.CombatVest,
            ApparelCatalog.CombatHelmet,
            ApparelCatalog.PoweredExoskeleton
        };
        ArmorConfig item = options.FirstOrDefault(candidate =>
            !_startingEquipment.Any(existing => ReferenceEquals(existing.Item, candidate))) ?? ApparelCatalog.WorkShirt;
        var next = new UnitStartingEquipment[_startingEquipment.Length + 1];
        Array.Copy(_startingEquipment, next, _startingEquipment.Length);
        next[^1] = new UnitStartingEquipment(item, 100f);
        _startingEquipment = next;
        _selectedEquipment = next.Length - 1;
        _status = $"Добавлен предмет: {item.Name}.";
    }

    private void RemoveStartingEquipment()
    {
        if (_startingEquipment.Length == 0)
            return;
        string name = _startingEquipment[_selectedEquipment].Item.Name;
        var next = new UnitStartingEquipment[_startingEquipment.Length - 1];
        if (_selectedEquipment > 0)
            Array.Copy(_startingEquipment, 0, next, 0, _selectedEquipment);
        if (_selectedEquipment < next.Length)
            Array.Copy(_startingEquipment, _selectedEquipment + 1, next, _selectedEquipment, next.Length - _selectedEquipment);
        _startingEquipment = next;
        _selectedEquipment = Math.Clamp(_selectedEquipment, 0, Math.Max(0, next.Length - 1));
        _status = $"Удалён предмет: {name}.";
    }

    private void ChangeStartingEquipmentItem(ArmorConfig item)
    {
        if (_startingEquipment.Length == 0)
            return;
        UnitStartingEquipment current = _startingEquipment[_selectedEquipment];
        _startingEquipment[_selectedEquipment] = new UnitStartingEquipment(item, current.Durability);
        _imguiNameEquipmentIndex = -1;
        _status = $"Предмет изменён: {item.Name}.";
    }

    private void UpdateStartingEquipment(Func<UnitStartingEquipment, UnitStartingEquipment> update)
    {
        if (_startingEquipment.Length == 0 || _selectedEquipment < 0 || _selectedEquipment >= _startingEquipment.Length)
            return;
        _startingEquipment[_selectedEquipment] = update(_startingEquipment[_selectedEquipment]);
    }

    private unsafe void DrawImGuiMarkerRoleCheckbox(string label, UnitAnatomyMarkerRole role)
    {
        if (_markersByRegion[_selectedPart].Length == 0)
            return;

        UnitAnatomyMarker marker = _markersByRegion[_selectedPart][_selectedMarker];
        byte enabled = (byte)((marker.Roles & role) != 0 ? 1 : 0);
        if (ImguiNative.igCheckbox(label, &enabled))
        {
            UnitAnatomyMarkerRole roles = enabled != 0
                ? marker.Roles | role
                : marker.Roles & ~role;
            UpdateSelectedMarker(current => CopyMarker(current, roles: roles));
        }
    }

    private unsafe void DrawImGuiStringCombo(
        string label,
        string current,
        IEnumerable<string> choices,
        Action<string> onChanged)
    {
        if (!ImguiNative.igBeginCombo(label, current, ImGuiComboFlags.None))
            return;

        foreach (string choice in choices)
        {
            if (string.IsNullOrWhiteSpace(choice))
                continue;
            bool selected = string.Equals(choice, current, StringComparison.Ordinal);
            if (ImguiNative.igSelectable_Bool(
                $"{choice}##{label}", selected, ImGuiSelectableFlags.None, new ImGuiVector2(0f, 0f)))
                onChanged(choice);
        }

        ImguiNative.igEndCombo();
    }

    private void AddOrganVolume()
    {
        if (_regionIds.Length == 0 || _tissuesByRegion.Length == 0)
            return;

        int host = Math.Clamp(_selectedPart, 0, _regionIds.Length - 1);
        HitRegionPartBinding[] hostTissues = _tissuesByRegion[host];
        if (hostTissues.Length == 0)
            return;

        string id = CreateUniqueOrganId();
        BodyPartDefinition part = _parts[host];
        var extents = new NumericsVector3(
            MathF.Max(0.005f, part.Scale.X * 0.22f),
            MathF.Max(0.005f, part.Scale.Y * 0.22f),
            MathF.Max(0.005f, part.Scale.Z * 0.22f));
        var organ = new UnitAnatomyVolume(
            id,
            _regionIds[host],
            hostTissues[0].Id,
            hostTissues[0].Id,
            NumericsVector3.Zero,
            extents);
        var next = new UnitAnatomyVolume[_organs.Length + 1];
        Array.Copy(_organs, next, _organs.Length);
        next[^1] = organ;
        _organs = next;
        _selectedOrgan = next.Length - 1;
        _status = $"Добавлен объём {id}; настрой центр, размеры и связи тканей.";
    }

    private string CreateUniqueOrganId()
    {
        for (int i = 1; i < 10000; i++)
        {
            string id = $"organ.custom.{i:000}";
            if (Array.FindIndex(_organs, item => string.Equals(item.Id, id, StringComparison.Ordinal)) < 0)
                return id;
        }
        return $"organ.custom.{Guid.NewGuid():N}";
    }

    private void RemoveOrganVolume()
    {
        if (_organs.Length == 0)
            return;

        string id = _organs[_selectedOrgan].Id;
        var next = new UnitAnatomyVolume[_organs.Length - 1];
        if (_selectedOrgan > 0)
            Array.Copy(_organs, 0, next, 0, _selectedOrgan);
        if (_selectedOrgan < next.Length)
            Array.Copy(_organs, _selectedOrgan + 1, next, _selectedOrgan, next.Length - _selectedOrgan);
        _organs = next;
        _selectedOrgan = Math.Clamp(_selectedOrgan, 0, Math.Max(0, _organs.Length - 1));
        _status = $"Удалён орган {id}.";
    }

    private void ChangeOrganParent(string regionId)
    {
        int host = Array.IndexOf(_regionIds, regionId);
        if (host < 0 || _tissuesByRegion[host].Length == 0)
            return;

        string entry = _tissuesByRegion[host][0].Id;
        UpdateSelectedOrgan(current => CopyVolume(
            current, parentRegionId: regionId, entryBindingId: entry));
    }

    private string[] GetAllTissueIds()
    {
        return _tissuesByRegion
            .SelectMany(items => items)
            .Select(item => item.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private string[] GetTissueIdsForRegion(string regionId)
    {
        int regionIndex = Array.IndexOf(_regionIds, regionId);
        return regionIndex >= 0
            ? _tissuesByRegion[regionIndex].Select(item => item.Id).ToArray()
            : Array.Empty<string>();
    }

    private void UpdateSelectedOrgan(Func<UnitAnatomyVolume, UnitAnatomyVolume> update)
    {
        if (_organs.Length == 0 || _selectedOrgan < 0 || _selectedOrgan >= _organs.Length)
            return;
        _organs[_selectedOrgan] = update(_organs[_selectedOrgan]);
        PruneInvalidOrgans();
    }

    private void AddTissueBinding()
    {
        int index = Math.Clamp(_selectedPart, 0, _regionIds.Length - 1);
        HitRegionPartBinding[] source = _tissuesByRegion[index];
        string id = CreateUniqueTissueId(index);
        string parentId = source.Length > 0 ? source[0].Id : string.Empty;
        var next = new HitRegionPartBinding[source.Length + 1];
        Array.Copy(source, next, source.Length);
        next[^1] = new HitRegionPartBinding(
            UnitHealthPartId.WholeBody,
            weight: 1f,
            kind: UnitHealthPartKind.BodyPart,
            maxHitPoints: 20f,
            id: id,
            parentId: string.IsNullOrWhiteSpace(parentId) ? null : parentId);
        _tissuesByRegion[index] = next;
        _selectedTissue = next.Length - 1;
        _status = $"Добавлена ткань {id}.";
    }

    private string CreateUniqueTissueId(int regionIndex)
    {
        for (int i = 1; i < 10000; i++)
        {
            string id = $"{_regionIds[regionIndex]}.tissue.custom.{i:000}";
            if (!_tissuesByRegion.SelectMany(items => items)
                .Any(item => string.Equals(item.Id, id, StringComparison.Ordinal)))
                return id;
        }
        return $"{_regionIds[regionIndex]}.tissue.custom.{Guid.NewGuid():N}";
    }

    private void RemoveTissueBinding()
    {
        int region = _selectedPart;
        HitRegionPartBinding[] current = _tissuesByRegion[region];
        if (current.Length <= 1)
            return;

        string removedId = current[_selectedTissue].Id;
        var next = new HitRegionPartBinding[current.Length - 1];
        if (_selectedTissue > 0)
            Array.Copy(current, 0, next, 0, _selectedTissue);
        if (_selectedTissue < next.Length)
            Array.Copy(current, _selectedTissue + 1, next, _selectedTissue, next.Length - _selectedTissue);
        _tissuesByRegion[region] = next;
        _selectedTissue = Math.Clamp(_selectedTissue, 0, next.Length - 1);
        _organs = _organs
            .Where(volume => !string.Equals(volume.EntryBindingId, removedId, StringComparison.Ordinal) &&
                             !string.Equals(volume.TargetBindingId, removedId, StringComparison.Ordinal))
            .ToArray();
        _selectedOrgan = Math.Clamp(_selectedOrgan, 0, Math.Max(0, _organs.Length - 1));
        _status = $"Удалена ткань {removedId}; органы, которые ссылались на неё, также удалены.";
    }

    private void UpdateSelectedTissue(Func<HitRegionPartBinding, HitRegionPartBinding> update)
    {
        HitRegionPartBinding[] current = _tissuesByRegion[_selectedPart];
        if (current.Length == 0 || _selectedTissue < 0 || _selectedTissue >= current.Length)
            return;
        var next = (HitRegionPartBinding[])current.Clone();
        next[_selectedTissue] = update(next[_selectedTissue]);
        _tissuesByRegion[_selectedPart] = next;
    }

    private void AddAnatomyMarker()
    {
        int region = Math.Clamp(_selectedPart, 0, _regionIds.Length - 1);
        UnitAnatomyMarker[] current = _markersByRegion[region];
        string id = CreateUniqueMarkerId(region);
        var next = new UnitAnatomyMarker[current.Length + 1];
        Array.Copy(current, next, current.Length);
        next[^1] = new UnitAnatomyMarker(id, UnitAnatomyMarkerRole.Target | UnitAnatomyMarkerRole.Aim);
        _markersByRegion[region] = next;
        _selectedMarker = next.Length - 1;
        _status = $"Добавлен маркер {id}.";
    }

    private string CreateUniqueMarkerId(int regionIndex)
    {
        for (int i = 1; i < 10000; i++)
        {
            string id = $"custom.marker.{i:000}";
            if (!_markersByRegion.SelectMany(items => items)
                .Any(item => string.Equals(item.Id, id, StringComparison.Ordinal)))
                return id;
        }
        return $"custom.marker.{Guid.NewGuid():N}";
    }

    private void RemoveAnatomyMarker()
    {
        int region = _selectedPart;
        UnitAnatomyMarker[] current = _markersByRegion[region];
        if (current.Length == 0)
            return;

        string removedId = current[_selectedMarker].Id;
        var next = new UnitAnatomyMarker[current.Length - 1];
        if (_selectedMarker > 0)
            Array.Copy(current, 0, next, 0, _selectedMarker);
        if (_selectedMarker < next.Length)
            Array.Copy(current, _selectedMarker + 1, next, _selectedMarker, next.Length - _selectedMarker);
        _markersByRegion[region] = next;
        _selectedMarker = Math.Clamp(_selectedMarker, 0, Math.Max(0, next.Length - 1));
        _status = $"Удалён маркер {removedId}.";
    }

    private void UpdateSelectedMarker(Func<UnitAnatomyMarker, UnitAnatomyMarker> update)
    {
        UnitAnatomyMarker[] current = _markersByRegion[_selectedPart];
        if (current.Length == 0 || _selectedMarker < 0 || _selectedMarker >= current.Length)
            return;
        var next = (UnitAnatomyMarker[])current.Clone();
        next[_selectedMarker] = update(next[_selectedMarker]);
        _markersByRegion[_selectedPart] = next;
    }

    private void PruneInvalidOrgans()
    {
        var knownRegions = new HashSet<string>(_regionIds, StringComparer.Ordinal);
        var knownTissues = new HashSet<string>(
            _tissuesByRegion.SelectMany(items => items).Select(item => item.Id),
            StringComparer.Ordinal);
        var filtered = new List<UnitAnatomyVolume>(_organs.Length);
        var knownVolumeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (UnitAnatomyVolume organ in _organs)
        {
            int parentIndex = Array.IndexOf(_regionIds, organ.ParentRegionId);
            if (parentIndex < 0 ||
                !_tissuesByRegion[parentIndex].Any(item => string.Equals(item.Id, organ.EntryBindingId, StringComparison.Ordinal)) ||
                !knownTissues.Contains(organ.TargetBindingId) ||
                !knownVolumeIds.Add(organ.Id))
                continue;
            filtered.Add(organ);
        }
        _organs = filtered.ToArray();
        _selectedOrgan = Math.Clamp(_selectedOrgan, 0, Math.Max(0, _organs.Length - 1));
    }

    private static HitRegionPartBinding CopyTissue(
        HitRegionPartBinding original,
        UnitHealthPartId? part = null,
        UnitHealthPartKind? kind = null,
        float? weight = null,
        float? maxHitPoints = null,
        UnitHealthPartId? parentPart = null,
        int? armorZone = null,
        float? projectileEnergyLoss = null,
        float? projectilePenetrationLoss = null) =>
        new(
            part ?? original.Part,
            weight ?? original.Weight,
            kind ?? original.Kind,
            maxHitPoints ?? original.MaxHitPoints,
            parentPart ?? original.ParentPart,
            original.Id,
            original.ParentId,
            armorZone ?? original.ArmorZone,
            projectileEnergyLoss ?? original.ProjectileEnergyLoss,
            projectilePenetrationLoss ?? original.ProjectilePenetrationLoss);

    private static UnitAnatomyMarker CopyMarker(
        UnitAnatomyMarker original,
        UnitAnatomyMarkerRole? roles = null,
        NumericsVector3? offset = null) =>
        new(original.Id, roles ?? original.Roles, offset ?? original.LocalOffset);

    private static UnitAnatomyVolume CopyVolume(
        UnitAnatomyVolume original,
        string? parentRegionId = null,
        string? entryBindingId = null,
        string? targetBindingId = null,
        NumericsVector3? center = null,
        NumericsVector3? halfExtents = null,
        BodyPrimitive? primitive = null,
        float? rotation = null,
        float? energyLoss = null,
        float? penetrationLoss = null,
        float? damageMultiplier = null,
        float? bleedMultiplier = null) =>
        new(
            original.Id,
            parentRegionId ?? original.ParentRegionId,
            entryBindingId ?? original.EntryBindingId,
            targetBindingId ?? original.TargetBindingId,
            center ?? original.LocalCenter,
            halfExtents ?? original.HalfExtents,
            primitive ?? original.Primitive,
            rotation ?? original.LocalRotation,
            energyLoss ?? original.EnergyLoss,
            penetrationLoss ?? original.PenetrationLoss,
            damageMultiplier ?? original.DamageMultiplier,
            bleedMultiplier ?? original.BleedMultiplier);

    private static T[][] CloneRows<T>(T[][] rows)
    {
        var result = new T[rows.Length][];
        for (int i = 0; i < rows.Length; i++)
            result[i] = (T[])rows[i].Clone();
        return result;
    }

    private unsafe void DrawImGuiFloatInput(
        string label,
        string id,
        float currentValue,
        Action<float> onChanged,
        float minimum,
        float maximum)
    {
        TextImGui(label);
        float value = currentValue;
        ImguiNative.igSetNextItemWidth(MathF.Max(90f, _propertiesPanel.Size.X - 34f));
        if (ImguiNative.igInputFloat(id, &value, 0f, 0f, "%.4f", ImGuiInputTextFlags.None))
            onChanged(Math.Clamp(value, minimum, maximum));
    }

    private unsafe void DrawImGuiEnumCombo<TEnum>(
        string label,
        TEnum current,
        Action<TEnum> onChanged)
        where TEnum : struct, Enum
    {
        if (!ImguiNative.igBeginCombo(label, current.ToString(), ImGuiComboFlags.None))
            return;

        foreach (TEnum option in Enum.GetValues<TEnum>())
        {
            bool selected = EqualityComparer<TEnum>.Default.Equals(option, current);
            if (ImguiNative.igSelectable_Bool(
                option.ToString(), selected, ImGuiSelectableFlags.None, new ImGuiVector2(0f, 0f)))
                onChanged(option);
        }

        ImguiNative.igEndCombo();
    }

    private void UpdateDefinition(
        float? moveSpeed = null,
        float? width = null,
        float? length = null,
        float? height = null,
        float? viewRange = null,
        float? fieldOfView = null,
        UnitBodyType? bodyType = null)
    {
        UnitDefinition current = _definition;
        _definition = new UnitDefinition(
            current.Type,
            bodyType ?? current.BodyType,
            moveSpeed ?? current.MoveSpeed,
            width ?? current.Width,
            length ?? current.Length,
            height ?? current.Height,
            viewRange ?? current.ViewRange,
            fieldOfView ?? current.FieldOfView);
    }

    private static UnitDefinition CreateDefaultColonistDefinition() =>
        new(UnitType.Colonist, UnitBodyType.Colonist, 2.8f, 0.35f, 0.60f, 1.75f, 20f, 110f);

    private unsafe void DrawImGuiPartNameInput()
    {
        PrepareImGuiNameBuffer();
        fixed (byte* text = _imguiPartNameBuffer)
        {
            if (ImguiNative.igInputText(
                "##BodyPartDisplayName", text, (uint)_imguiPartNameBuffer.Length,
                ImGuiInputTextFlags.None, null, null))
            {
                int end = Array.IndexOf(_imguiPartNameBuffer, (byte)0);
                if (end < 0) end = _imguiPartNameBuffer.Length;
                string name = Encoding.UTF8.GetString(_imguiPartNameBuffer, 0, end).Trim();
                _parts[_selectedPart] = Copy(_parts[_selectedPart], displayName: name);
            }
        }
    }

    private void PrepareImGuiNameBuffer()
    {
        if (_imguiNamePartIndex == _selectedPart) return;
        Array.Fill(_imguiPartNameBuffer, (byte)0);
        byte[] current = Encoding.UTF8.GetBytes(_parts[_selectedPart].DisplayName ?? string.Empty);
        Array.Copy(current, _imguiPartNameBuffer, Math.Min(current.Length, _imguiPartNameBuffer.Length - 1));
        _imguiNamePartIndex = _selectedPart;
    }

    private unsafe void DrawImGuiProjectionHeaders()
    {
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse;
        for (int i = 0; i < _previewPanels.Length; i++)
        {
            RectangleShape panel = _previewPanels[i];
            BeginImGuiWindow($"##ProjectionHeader{i}", panel.Position, new Vector2f(panel.Size.X, 28f), flags);
            TextImGui(i switch { 0 => "СПЕРЕДИ · Y / Z", 1 => "СБОКУ · X / Z", _ => "СВЕРХУ · X / Y" });
            ImguiNative.igSameLine(0f, 10f);
            byte symmetric = (byte)(_symmetryEnabled[i] ? 1 : 0);
            if (ImguiNative.igCheckbox($"Симметрия##sym{i}", &symmetric))
            {
                _symmetryEnabled[i] = symmetric != 0;
                if (_symmetryEnabled[i]) ApplyEnabledSymmetry(i);
            }
            ImguiNative.igEnd();
        }
    }

    private static unsafe void BeginImGuiWindow(string id, Vector2f position, Vector2f size, ImGuiWindowFlags flags)
    {
        ImguiNative.igSetNextWindowPos(new ImGuiVector2(position.X, position.Y), ImGuiCond.Always, new ImGuiVector2(0f, 0f));
        ImguiNative.igSetNextWindowSize(new ImGuiVector2(size.X, size.Y), ImGuiCond.Always);
        ImguiNative.igBegin(id, null, flags);
    }

    private static unsafe void TextImGui(string text) => ImguiNative.igTextUnformatted(text, null);
    private static unsafe bool ButtonImGui(string label, float width = 0f) => ImguiNative.igButton(label, new ImGuiVector2(width, 0f));

    private void SelectPartFromImGui(int index)
    {
        if (index < 0 || index >= _parts.Length) return;
        if (_parentSelectionActive)
        {
            int childIndex = _parentSourcePart;
            if (index == childIndex)
            {
                _parentSelectionActive = false;
                _parentSourcePart = -1;
                _status = "Выбор родителя отменён.";
                return;
            }
            if (index >= childIndex)
            {
                _status = "Родитель должен быть выше дочерней части в списке.";
                return;
            }
            BindPartToParentPreservingLayout(childIndex, index);
            _selectedPart = childIndex;
            _parentSelectionActive = false;
            _parentSourcePart = -1;
            _imguiNamePartIndex = -1;
            RefreshProjectedGeometry();
            _status = $"Назначен родитель «{PartName(_regionIds[index])}».";
            return;
        }
        if (_symmetryToolActive)
        {
            if (index == _symmetrySourcePart)
            {
                ClearSymmetryPair(index);
                _status = $"Симметрия для «{PartName(_regionIds[index])}» снята.";
            }
            else
            {
                int source = _symmetrySourcePart;
                SetSymmetryPair(source, index);
                _selectedPart = index;
                _status = $"«{PartName(_regionIds[source])}» и «{PartName(_regionIds[index])}» связаны зеркально.";
            }
            _symmetryToolActive = false;
            _symmetrySourcePart = -1;
            _imguiNamePartIndex = -1;
            RefreshProjectedGeometry();
            return;
        }
        _selectedPart = index;
        _editingProperty = -1;
        _editingPartName = false;
        _imguiNamePartIndex = -1;
        RefreshProjectedGeometry();
    }

    private bool IsImGuiUiPoint(Vector2i position)
    {
        if (_header.GetGlobalBounds().Contains(position) ||
            _listPanel.GetGlobalBounds().Contains(position) ||
            _propertiesPanel.GetGlobalBounds().Contains(position))
            return true;
        for (int i = 0; i < _previewPanels.Length; i++)
        {
            FloatRect panel = _previewPanels[i].GetGlobalBounds();
            if (position.X >= panel.Left && position.X <= panel.Left + panel.Width &&
                position.Y >= panel.Top && position.Y <= panel.Top + 28f)
                return true;
        }
        return false;
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
        _listVisibleRows = Math.Clamp((int)((contentHeight - 225f) / _listRowHeight), 4, MaxParts);
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

        _addRegionButton.Position = new Vector2f(listX + 5f, contentY + contentHeight - 185f);
        _addRegionButton.Size = new Vector2f(listWidth - 10f, 25f);
        _addAllFormsButton.Position = new Vector2f(listX + 5f, contentY + contentHeight - 154f);
        _addAllFormsButton.Size = new Vector2f(listWidth - 10f, 25f);
        _removeRegionButton.Position = new Vector2f(listX + 5f, contentY + contentHeight - 123f);
        _removeRegionButton.Size = new Vector2f(listWidth - 10f, 25f);
        _copyPartButton.Position = new Vector2f(listX + 5f, contentY + contentHeight - 92f);
        _copyPartButton.Size = new Vector2f(listWidth - 10f, 25f);
        _symmetryToolButton.Position = new Vector2f(listX + 5f, contentY + contentHeight - 61f);
        _symmetryToolButton.Size = new Vector2f(listWidth - 10f, 25f);
        _detachAllParentsButton.Position = new Vector2f(listX + 5f, contentY + contentHeight - 30f);
        _detachAllParentsButton.Size = new Vector2f(listWidth - 10f, 25f);

        if (_addRegionLabel != null)
            _addRegionLabel.Position = new Vector2f(_addRegionButton.Position.X + 8f, _addRegionButton.Position.Y + 6f);
        if (_addAllFormsLabel != null)
            _addAllFormsLabel.Position = new Vector2f(_addAllFormsButton.Position.X + 8f, _addAllFormsButton.Position.Y + 6f);
        if (_removeRegionLabel != null)
            _removeRegionLabel.Position = new Vector2f(_removeRegionButton.Position.X + 8f, _removeRegionButton.Position.Y + 6f);
        if (_copyPartLabel != null)
            _copyPartLabel.Position = new Vector2f(_copyPartButton.Position.X + 8f, _copyPartButton.Position.Y + 6f);
        if (_symmetryToolLabel != null)
            _symmetryToolLabel.Position = new Vector2f(_symmetryToolButton.Position.X + 8f, _symmetryToolButton.Position.Y + 6f);
        if (_detachAllParentsLabel != null)
            _detachAllParentsLabel.Position = new Vector2f(_detachAllParentsButton.Position.X + 8f, _detachAllParentsButton.Position.Y + 6f);

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
        _propertiesViewportTop = contentY + MathF.Min(82f, propertyTopOffset - 2f);
        _propertiesViewportBottom = contentY + contentHeight - 8f;

        float basePrimitiveY = propertyTop + PropertyCount * _propertyRowHeight + 8f;
        _propertiesMaxScroll = MathF.Max(0f, basePrimitiveY + 278f - _propertiesViewportBottom);
        _propertiesScrollOffset = Math.Clamp(_propertiesScrollOffset, 0f, _propertiesMaxScroll);

        for (int i = 0; i < PropertyCount; i++)
        {
            float rowY = propertyTop + i * _propertyRowHeight - _propertiesScrollOffset;
            _sliderTracks[i].Position = new Vector2f(propertiesX + 12f, rowY + 25f);
            _sliderTracks[i].Size = new Vector2f(propertiesWidth - 24f, 6f);
        }

        _primitiveButton.Position = new Vector2f(propertiesX + 10f, basePrimitiveY - _propertiesScrollOffset);
        _primitiveButton.Size = new Vector2f(propertiesWidth - 20f, 30f);
        _parentButton.Position = new Vector2f(propertiesX + 10f, _primitiveButton.Position.Y + 35f);
        _parentButton.Size = new Vector2f(propertiesWidth - 20f, 26f);
        _detachParentButton.Position = new Vector2f(propertiesX + 10f, _parentButton.Position.Y + 31f);
        _detachParentButton.Size = new Vector2f(propertiesWidth - 20f, 26f);
        _allSymmetryButton.Position = new Vector2f(propertiesX + 10f, _detachParentButton.Position.Y + 31f);
        _allSymmetryButton.Size = new Vector2f(propertiesWidth - 20f, 26f);
        _gridButton.Position = new Vector2f(propertiesX + 10f, _allSymmetryButton.Position.Y + 31f);
        _gridButton.Size = new Vector2f(54f, 26f);
        _gridStepButton.Position = new Vector2f(_gridButton.Position.X + _gridButton.Size.X + 4f, _gridButton.Position.Y);
        _gridStepButton.Size = new Vector2f(48f, 26f);
        _surfaceSnapButton.Position = new Vector2f(_gridStepButton.Position.X + _gridStepButton.Size.X + 4f, _gridButton.Position.Y);
        _surfaceSnapButton.Size = new Vector2f(MathF.Max(54f, propertiesX + propertiesWidth - 10f - _surfaceSnapButton.Position.X), 26f);
        _viewNormalizationButton.Position = new Vector2f(propertiesX + 10f, _gridButton.Position.Y + 31f);
        _viewNormalizationButton.Size = new Vector2f(propertiesWidth - 20f, 26f);
        _resetCenterButton.Position = new Vector2f(propertiesX + 10f, _viewNormalizationButton.Position.Y + 31f);
        _resetCenterButton.Size = new Vector2f(propertiesWidth - 20f, 26f);
        _symmetryClearButton.Position = new Vector2f(propertiesX + 10f, _resetCenterButton.Position.Y + 31f);
        _symmetryClearButton.Size = new Vector2f(propertiesWidth - 20f, 26f);
        if (_symmetryClearLabel != null)
            _symmetryClearLabel.Position = new Vector2f(_symmetryClearButton.Position.X + 6f, _symmetryClearButton.Position.Y + 6f);
        _renamePartButton.Position = new Vector2f(propertiesX + 10f, _symmetryClearButton.Position.Y + 31f);
        _renamePartButton.Size = new Vector2f(propertiesWidth - 20f, 26f);

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
        // Projection headers and their controls are now ImGui windows.

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
        float pixelsPerUnit = (_normalizeViewsEnabled
            ? _normalizedPixelsPerUnit
            : MathF.Max(1f, MathF.Min(
                MathF.Max(1f, layout.Width - 12f) / spanX,
                MathF.Max(1f, layout.Height - 12f) / spanY))) * _projectionZoom[projection];
        float worldCenterX = (minX + maxX) * 0.5f;
        float worldCenterY = (minY + maxY) * 0.5f;
        float screenCenterX = layout.Left + layout.Width * 0.5f + _projectionPanX[projection];
        float screenCenterY = layout.Top + layout.Height * 0.5f + _projectionPanY[projection];
        layout = layout.WithTransform(pixelsPerUnit, screenCenterX, screenCenterY, worldCenterX, worldCenterY);
        _projectionLayouts[projection] = layout;

        View baseView = window.GetView();
        View? clippedView = CreateClippedView(
            window, baseView, layout.Left, layout.Top, layout.Width, layout.Height);
        if (clippedView != null)
            window.SetView(clippedView);

        try
        {
            DrawGrid(window, layout, projection);
            int[] drawOrder = GetProjectionDrawOrder(projection);

            for (int drawIndex = 0; drawIndex < drawOrder.Length; drawIndex++)
            {
                int i = drawOrder[drawIndex];
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
        finally
        {
            if (clippedView != null)
            {
                window.SetView(baseView);
                clippedView.Dispose();
            }
        }
    }

    private void DrawProperty(RenderWindow window, int index)
    {
        RectangleShape track = _sliderTracks[index];
        if (!IsPropertiesItemVisible(track.Position.Y - 23f, 42f))
            return;

        PropertyInfo info = GetProperty(index);
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
            _sliderValues[index]!.DisplayedString = _editingProperty == index
                ? _propertyEditBuffer + "_"
                : info.Format;
            _sliderValues[index]!.FillColor = _editingProperty == index
                ? SelectedColor
                : new Color(226, 233, 241, 255);
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

    private bool IsPropertiesItemVisible(float itemY, float itemHeight) =>
        itemY >= _propertiesViewportTop &&
        itemY + itemHeight <= _propertiesViewportBottom;

    private float CalculateNormalizedProjectionScale()
    {
        float commonScale = float.PositiveInfinity;
        for (int projection = 0; projection < _projectionLayouts.Length; projection++)
        {
            ProjectionLayout layout = _projectionLayouts[projection];
            float minX = float.PositiveInfinity;
            float minY = float.PositiveInfinity;
            float maxX = float.NegativeInfinity;
            float maxY = float.NegativeInfinity;

            for (int i = 0; i < _parts.Length; i++)
            {
                ProjectedPart geometry = Project(
                    _parts[i], _worldPositions[i], _worldOrientations[i], projection);
                minX = MathF.Min(minX, geometry.X - geometry.HalfWidth);
                minY = MathF.Min(minY, geometry.Y - geometry.HalfHeight);
                maxX = MathF.Max(maxX, geometry.X + geometry.HalfWidth);
                maxY = MathF.Max(maxY, geometry.Y + geometry.HalfHeight);
            }

            float spanX = MathF.Max(0.01f, maxX - minX);
            float spanY = MathF.Max(0.01f, maxY - minY);
            float viewScale = MathF.Max(1f, MathF.Min(
                MathF.Max(1f, layout.Width - 12f) / spanX,
                MathF.Max(1f, layout.Height - 12f) / spanY));
            commonScale = MathF.Min(commonScale, viewScale);
        }

        return float.IsFinite(commonScale) ? MathF.Max(1f, commonScale) : 1f;
    }

    private void ResetSelectedPartToBodyCenter()
    {
        if (_selectedPart < 0 || _selectedPart >= _parts.Length)
            return;

        CalculateWorldTransforms();
        BodyPartDefinition part = _parts[_selectedPart];
        Quaternion parentOrientation =
            part.Anchor == BodyPartAnchor.Parent && part.ParentIndex >= 0
                ? _worldOrientations[part.ParentIndex]
                : Quaternion.Identity;
        NumericsVector3 worldDelta = -_worldPositions[_selectedPart];
        NumericsVector3 localDelta = NumericsVector3.Transform(
            worldDelta, Quaternion.Inverse(parentOrientation));

        _parts[_selectedPart] = Copy(
            part,
            localPosition: part.LocalPosition + localDelta);
        ApplyEnabledSymmetry();
        RefreshProjectedGeometry();
        _status = $"«{PartName(_regionIds[_selectedPart])}» перемещена в центр тела (0, 0, 0). Нажми Ctrl+S.";
    }

    private void ApplySizeSymmetry()
    {
        if (_selectedPart < 0 || _selectedPart >= _regionIds.Length)
            return;

        int counterpartIndex = GetSymmetryCounterpartIndex(_selectedPart);
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
        // Inverse of BodyPartTransformMath.FromEulerRadians, which uses
        // Quaternion.CreateFromYawPitchRoll(y, x, z). Previous extraction used
        // XYZ-order formulas, so quaternion round-trips changed angles and shifted
        // parts when detaching them from parents or aligning them to a surface.
        q = Quaternion.Normalize(q);

        float sinX = Math.Clamp(2f * (q.W * q.X - q.Y * q.Z), -1f, 1f);
        float x = MathF.Asin(sinX);
        float y = MathF.Atan2(
            2f * (q.W * q.Y + q.X * q.Z),
            1f - 2f * (q.X * q.X + q.Y * q.Y));
        float z = MathF.Atan2(
            2f * (q.W * q.Z + q.X * q.Y),
            1f - 2f * (q.X * q.X + q.Z * q.Z));

        return new NumericsVector3(x, y, z);
    }

    private static float WrapAngle(float angle)
    {
        while (angle > MathF.PI) angle -= MathF.Tau;
        while (angle < -MathF.PI) angle += MathF.Tau;
        return angle;
    }

    private int[] GetProjectionDrawOrder(int projection)
    {
        int[] order = new int[_projected[projection].Length];
        for (int i = 0; i < order.Length; i++)
            order[i] = i;

        Array.Sort(order, (left, right) =>
        {
            int depthOrder = _projected[projection][left].Depth.CompareTo(
                _projected[projection][right].Depth);
            return depthOrder != 0 ? depthOrder : left.CompareTo(right);
        });

        return order;
    }

    private int HitTestPreview(int projection, int x, int y)
    {
        ProjectedPart[] geometry = _projected[projection];
        int[] drawOrder = GetProjectionDrawOrder(projection);

        // Selection order is the reverse of drawing order: nearest/uppermost shape wins.
        for (int orderIndex = drawOrder.Length - 1; orderIndex >= 0; orderIndex--)
        {
            int partIndex = drawOrder[orderIndex];
            if (ContainsProjectedPart(geometry[partIndex], x, y))
                return partIndex;
        }

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

    private void HandleNumericEditKey(Keyboard.Key key)
    {
        if (_editingProperty < 0)
            return;

        if (key == Keyboard.Key.Enter)
        {
            string normalized = _propertyEditBuffer.Replace(',', '.');
            if (float.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) &&
                float.IsFinite(value))
            {
                int property = _editingProperty;
                _editingProperty = -1;
                SetProperty(property, value);
                _status = $"Параметр применён: {PartName(_regionIds[_selectedPart])}. Ctrl+S — сохранить.";
            }
            else
            {
                _editingProperty = -1;
                _status = "Некорректное число; значение не изменено.";
            }
            return;
        }

        if (key == Keyboard.Key.Escape)
        {
            _editingProperty = -1;
            _status = "Ручной ввод отменён.";
            return;
        }

        if (key == Keyboard.Key.Backspace)
        {
            if (_propertyEditBuffer.Length > 0)
                _propertyEditBuffer = _propertyEditBuffer[..^1];
            return;
        }

        char? character = key switch
        {
            Keyboard.Key.Num0 or Keyboard.Key.Numpad0 => '0',
            Keyboard.Key.Num1 or Keyboard.Key.Numpad1 => '1',
            Keyboard.Key.Num2 or Keyboard.Key.Numpad2 => '2',
            Keyboard.Key.Num3 or Keyboard.Key.Numpad3 => '3',
            Keyboard.Key.Num4 or Keyboard.Key.Numpad4 => '4',
            Keyboard.Key.Num5 or Keyboard.Key.Numpad5 => '5',
            Keyboard.Key.Num6 or Keyboard.Key.Numpad6 => '6',
            Keyboard.Key.Num7 or Keyboard.Key.Numpad7 => '7',
            Keyboard.Key.Num8 or Keyboard.Key.Numpad8 => '8',
            Keyboard.Key.Num9 or Keyboard.Key.Numpad9 => '9',
            Keyboard.Key.Period or Keyboard.Key.Comma => '.',
            Keyboard.Key.Hyphen or Keyboard.Key.Subtract => '-',
            _ => null
        };

        if (character == '.')
        {
            if (!_propertyEditBuffer.Contains('.'))
                _propertyEditBuffer += '.';
        }
        else if (character == '-' && _propertyEditBuffer.Length == 0)
        {
            _propertyEditBuffer = "-";
        }
        else if (character.HasValue && character.Value is >= '0' and <= '9' &&
                 _propertyEditBuffer.Length < 16)
        {
            _propertyEditBuffer += character.Value;
        }
    }

    private void Save()
    {
        _openedRegionIds = (string[])_regionIds.Clone();
        _openedParts = (BodyPartDefinition[])_parts.Clone();
        _saveRequested(
            (string[])_regionIds.Clone(),
            (BodyPartDefinition[])_parts.Clone(),
            _definition,
            (float[])_hitHalfHeights.Clone(),
            CloneRows(_tissuesByRegion),
            CloneRows(_markersByRegion),
            (UnitAnatomyVolume[])_organs.Clone(),
            (UnitStartingEquipment[])_startingEquipment.Clone());
    }

    private void CopySelectedPart()
    {
        if (_parts.Length >= MaxParts)
        {
            _status = $"Достигнут предел в {MaxParts} частей тела.";
            return;
        }

        if (_selectedPart < 0 || _selectedPart >= _parts.Length)
            return;

        string sourceName = PartName(_regionIds[_selectedPart]);
        string safeSourceId = _regionIds[_selectedPart].Replace('.', '-').Replace(' ', '-');
        string duplicateId = CreateUniqueRegionId($"custom.copy.{safeSourceId}");
        BodyPartDefinition duplicate = Copy(_parts[_selectedPart], symmetryPartnerId: string.Empty);
        AppendRegions(new[] { duplicateId }, new[] { duplicate });
        _status = $"Скопировано «{sourceName}». Копия выбрана; родитель и параметры сохранены, симметрию назначь отдельно.";
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

    private bool DetachAllParentsPreservingLayout()
    {
        if (_parts.Length == 0)
            return false;

        bool needsReset = false;
        for (int i = 0; i < _parts.Length; i++)
        {
            BodyPartDefinition part = _parts[i];
            BodyPartAnchor rootAnchor = part.Anchor == BodyPartAnchor.UnitHead
                ? BodyPartAnchor.UnitHead
                : BodyPartAnchor.UnitBody;
            needsReset |= part.ParentIndex >= 0 || part.Anchor != rootAnchor;
        }

        if (!needsReset)
            return false;

        CalculateWorldTransforms();
        var detached = new BodyPartDefinition[_parts.Length];
        for (int i = 0; i < _parts.Length; i++)
        {
            BodyPartDefinition part = _parts[i];
            BodyPartAnchor rootAnchor = part.Anchor == BodyPartAnchor.UnitHead
                ? BodyPartAnchor.UnitHead
                : BodyPartAnchor.UnitBody;
            NumericsVector3 jointPosition = _worldPositions[i];
            if (part.Pivot == BodyPartPivot.Start)
            {
                jointPosition -= NumericsVector3.Transform(
                    new NumericsVector3(MathF.Abs(part.Scale.X), 0f, 0f),
                    _worldOrientations[i]);
            }

            NumericsVector3 euler = QuaternionToEuler(_worldOrientations[i]);
            detached[i] = Copy(
                part,
                localPosition: jointPosition,
                parentIndex: -1,
                anchor: rootAnchor,
                localRotationX: euler.X,
                localRotationY: euler.Y,
                localRotation: euler.Z);
        }

        _parts = detached;
        RefreshProjectedGeometry();
        return true;
    }

    private bool DetachPartPreservingLayout(int partIndex)
    {
        if (partIndex < 0 || partIndex >= _parts.Length)
            return false;

        BodyPartDefinition part = _parts[partIndex];
        BodyPartAnchor rootAnchor = part.Anchor == BodyPartAnchor.UnitHead
            ? BodyPartAnchor.UnitHead
            : BodyPartAnchor.UnitBody;
        if (part.ParentIndex < 0 && part.Anchor == rootAnchor)
            return false;

        CalculateWorldTransforms();
        NumericsVector3 jointPosition = _worldPositions[partIndex];
        if (part.Pivot == BodyPartPivot.Start)
        {
            jointPosition -= NumericsVector3.Transform(
                new NumericsVector3(MathF.Abs(part.Scale.X), 0f, 0f),
                _worldOrientations[partIndex]);
        }

        NumericsVector3 euler = QuaternionToEuler(_worldOrientations[partIndex]);
        _parts[partIndex] = Copy(
            part,
            localPosition: jointPosition,
            parentIndex: -1,
            anchor: rootAnchor,
            localRotationX: euler.X,
            localRotationY: euler.Y,
            localRotation: euler.Z);
        return true;
    }

    private void BindPartToParentPreservingLayout(int childIndex, int parentIndex)
    {
        if (childIndex < 0 || childIndex >= _parts.Length ||
            parentIndex < 0 || parentIndex >= childIndex)
            return;

        CalculateWorldTransforms();

        BodyPartDefinition child = _parts[childIndex];
        NumericsVector3 childJointPosition = _worldPositions[childIndex];
        if (child.Pivot == BodyPartPivot.Start)
        {
            childJointPosition -= NumericsVector3.Transform(
                new NumericsVector3(MathF.Abs(child.Scale.X), 0f, 0f),
                _worldOrientations[childIndex]);
        }

        NumericsVector3 relativePosition = NumericsVector3.Transform(
            childJointPosition - _worldPositions[parentIndex],
            Quaternion.Inverse(_worldOrientations[parentIndex]));
        Quaternion relativeOrientation = Quaternion.Normalize(
            Quaternion.Inverse(_worldOrientations[parentIndex]) * _worldOrientations[childIndex]);
        NumericsVector3 euler = QuaternionToEuler(relativeOrientation);
        _parts[childIndex] = Copy(
            child,
            localPosition: relativePosition,
            parentIndex: parentIndex,
            anchor: BodyPartAnchor.Parent,
            localRotationX: euler.X,
            localRotationY: euler.Y,
            localRotation: euler.Z);
    }

    private static NumericsVector3 QuaternionToEuler(Quaternion orientation)
    {
        orientation = Quaternion.Normalize(orientation);
        float sinPitch = Math.Clamp(
            2f * (orientation.W * orientation.X - orientation.Y * orientation.Z),
            -1f, 1f);
        float pitch = MathF.Asin(sinPitch);
        float yaw;
        float roll;
        if (MathF.Abs(MathF.Cos(pitch)) > 0.0001f)
        {
            yaw = MathF.Atan2(
                2f * (orientation.X * orientation.Z + orientation.W * orientation.Y),
                1f - 2f * (orientation.X * orientation.X + orientation.Y * orientation.Y));
            roll = MathF.Atan2(
                2f * (orientation.X * orientation.Y + orientation.W * orientation.Z),
                1f - 2f * (orientation.X * orientation.X + orientation.Z * orientation.Z));
        }
        else
        {
            yaw = MathF.Atan2(
                2f * (orientation.W * orientation.Y - orientation.X * orientation.Z),
                1f - 2f * (orientation.Y * orientation.Y + orientation.Z * orientation.Z));
            roll = 0f;
        }

        return new NumericsVector3(pitch, yaw, roll);
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

        int counterpartIndex = GetSymmetryCounterpartIndex(_selectedPart);
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

    private int GetSymmetryCounterpartIndex(int partIndex)
    {
        if (partIndex < 0 || partIndex >= _parts.Length)
            return -1;

        string? explicitPartner = _parts[partIndex].SymmetryPartnerId;
        if (explicitPartner != null)
        {
            if (explicitPartner.Length == 0)
                return -1;

            int explicitIndex = Array.IndexOf(_regionIds, explicitPartner);
            return explicitIndex >= 0 && explicitIndex != partIndex ? explicitIndex : -1;
        }

        string? inferredPartner = GetCounterpartId(_regionIds[partIndex]);
        if (inferredPartner == null)
            return -1;

        int inferredIndex = Array.IndexOf(_regionIds, inferredPartner);
        return inferredIndex >= 0 && inferredIndex != partIndex ? inferredIndex : -1;
    }

    private int CountSymmetryPairs()
    {
        int count = 0;
        for (int i = 0; i < _parts.Length; i++)
        {
            int partner = GetSymmetryCounterpartIndex(i);
            if (partner > i)
                count++;
        }
        return count;
    }

    private void SetSymmetryPair(int sourceIndex, int targetIndex)
    {
        if (sourceIndex < 0 || targetIndex < 0 ||
            sourceIndex >= _parts.Length || targetIndex >= _parts.Length ||
            sourceIndex == targetIndex)
            return;

        ClearSymmetryPair(sourceIndex);
        ClearSymmetryPair(targetIndex);
        Array.Fill(_symmetryEnabled, true);
        _parts[sourceIndex] = Copy(_parts[sourceIndex], symmetryPartnerId: _regionIds[targetIndex]);
        _parts[targetIndex] = Copy(_parts[targetIndex], symmetryPartnerId: _regionIds[sourceIndex]);

        BodyPartDefinition source = _parts[sourceIndex];
        BodyPartDefinition target = _parts[targetIndex];
        NumericsVector3 mirroredPosition = new(
            source.LocalPosition.X,
            -source.LocalPosition.Y,
            source.LocalPosition.Z);
        _parts[targetIndex] = Copy(
            target,
            localPosition: mirroredPosition,
            scale: source.Scale,
            localRotation: -source.LocalRotation,
            localRotationX: -source.LocalRotationX,
            localRotationY: source.LocalRotationY,
            primitive: source.Primitive);
    }

    private void ClearSymmetryPair(int partIndex)
    {
        if (partIndex < 0 || partIndex >= _parts.Length)
            return;

        int partnerIndex = GetSymmetryCounterpartIndex(partIndex);
        _parts[partIndex] = Copy(_parts[partIndex], symmetryPartnerId: string.Empty);
        if (partnerIndex >= 0 && partnerIndex < _parts.Length)
            _parts[partnerIndex] = Copy(_parts[partnerIndex], symmetryPartnerId: string.Empty);
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
        float? localRotationY = null,
        string? symmetryPartnerId = null,
        string? displayName = null,
        BodyPartMotion? motion = null,
        float? followDistance = null,
        BodyPartRole? role = null,
        BodyPartPivot? pivot = null) =>
        new(
            localPosition ?? original.LocalPosition,
            scale ?? original.Scale,
            primitive ?? original.Primitive,
            parentIndex: parentIndex ?? original.ParentIndex,
            localRotation: localRotation ?? original.LocalRotation,
            motion: motion ?? original.Motion,
            followDistance: followDistance ?? original.FollowDistance,
            anchor: anchor ?? original.Anchor,
            role: role ?? original.Role,
            pivot: pivot ?? original.Pivot,
            localRotationX: localRotationX ?? original.LocalRotationX,
            localRotationY: localRotationY ?? original.LocalRotationY,
            symmetryPartnerId: symmetryPartnerId ?? original.SymmetryPartnerId,
            displayName: displayName ?? original.DisplayName);

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

        int depthAxis = projection switch { 0 => 0, 1 => 1, _ => 2 };
        float depth = Component(position, depthAxis);

        return new ProjectedPart(
            x,
            y,
            depth,
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

    private static View? CreateClippedView(
        RenderWindow window,
        View baseView,
        float left,
        float top,
        float width,
        float height)
    {
        Vector2u windowSize = window.Size;
        if (windowSize.X == 0 || windowSize.Y == 0 || width <= 0f || height <= 0f)
            return null;

        Vector2i topLeft = window.MapCoordsToPixel(new Vector2f(left, top), baseView);
        Vector2i bottomRight = window.MapCoordsToPixel(
            new Vector2f(left + width, top + height), baseView);

        float clipLeft = Math.Clamp(MathF.Min(topLeft.X, bottomRight.X), 0f, windowSize.X);
        float clipTop = Math.Clamp(MathF.Min(topLeft.Y, bottomRight.Y), 0f, windowSize.Y);
        float clipRight = Math.Clamp(MathF.Max(topLeft.X, bottomRight.X), 0f, windowSize.X);
        float clipBottom = Math.Clamp(MathF.Max(topLeft.Y, bottomRight.Y), 0f, windowSize.Y);
        float clipWidth = clipRight - clipLeft;
        float clipHeight = clipBottom - clipTop;
        if (clipWidth < 1f || clipHeight < 1f)
            return null;

        FloatRect baseViewport = baseView.Viewport;
        float viewportLeft = baseViewport.Left * windowSize.X;
        float viewportTop = baseViewport.Top * windowSize.Y;
        float viewportWidth = baseViewport.Width * windowSize.X;
        float viewportHeight = baseViewport.Height * windowSize.Y;
        if (viewportWidth < 1f || viewportHeight < 1f)
            return null;

        float centerPixelX = (clipLeft + clipRight) * 0.5f;
        float centerPixelY = (clipTop + clipBottom) * 0.5f;
        float baseCenterPixelX = viewportLeft + viewportWidth * 0.5f;
        float baseCenterPixelY = viewportTop + viewportHeight * 0.5f;

        float worldCenterX = baseView.Center.X +
            (centerPixelX - baseCenterPixelX) / viewportWidth * baseView.Size.X;
        float worldCenterY = baseView.Center.Y +
            (centerPixelY - baseCenterPixelY) / viewportHeight * baseView.Size.Y;
        float worldWidth = baseView.Size.X * clipWidth / viewportWidth;
        float worldHeight = baseView.Size.Y * clipHeight / viewportHeight;

        return new View(
            new Vector2f(worldCenterX, worldCenterY),
            new Vector2f(MathF.Max(0.01f, worldWidth), MathF.Max(0.01f, worldHeight)))
        {
            Viewport = new FloatRect(
                new Vector2f(clipLeft / windowSize.X, clipTop / windowSize.Y),
                new Vector2f(clipWidth / windowSize.X, clipHeight / windowSize.Y))
        };
    }

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

    private string PartName(string id)
    {
        for (int i = 0; i < _regionIds.Length && i < _parts.Length; i++)
        {
            if (string.Equals(_regionIds[i], id, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(_parts[i].DisplayName))
                return _parts[i].DisplayName!;
        }

        return id switch
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
    }

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
        _viewNormalizationLabel?.Dispose();
        _resetCenterLabel?.Dispose();
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

        _imguiBackend?.Dispose();
        _imguiBackend = null;
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
        _viewNormalizationButton.Dispose();
        _resetCenterButton.Dispose();
        _detachParentButton.Dispose();
        _detachAllParentsButton.Dispose();
        _detachParentLabel?.Dispose();
        _detachAllParentsLabel?.Dispose();
        _symmetryClearButton.Dispose();
        _renamePartButton.Dispose();
        _copyPartButton.Dispose();
        _symmetryToolButton.Dispose();
        _copyPartLabel?.Dispose();
        _symmetryToolLabel?.Dispose();
        _symmetryClearLabel?.Dispose();
        _renamePartLabel?.Dispose();
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
        public float Depth;
        public float HalfWidth;
        public float HalfHeight;
        public float ScreenRotation;
        public BodyPrimitive Primitive;
        public float ScreenX;
        public float ScreenY;
        public float ScreenHalfWidth;
        public float ScreenHalfHeight;

        public ProjectedPart(float x, float y, float depth, float halfWidth, float halfHeight, float screenRotation, BodyPrimitive primitive)
        {
            X = x;
            Y = y;
            Depth = depth;
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
