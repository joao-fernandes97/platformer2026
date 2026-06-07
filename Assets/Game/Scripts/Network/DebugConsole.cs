using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Captures Unity log messages and renders them as an on-screen overlay.
/// </summary>
public class DebugConsole : MonoBehaviour
{
    [Header("Display")]
    public int maxLines = 50;
    public int fontSize = 13;

    public KeyCode toggleKey = KeyCode.BackQuote;

    public bool visibleOnStart = true;

    // Runtime state
    private readonly List<(string text, LogType type)> _lines = new();
    private bool   _visible;
    private Vector2 _scroll;

    // Cached GUIStyles
    private GUIStyle _boxStyle;
    private GUIStyle _logStyle;
    private GUIStyle _warnStyle;
    private GUIStyle _errorStyle;
    private GUIStyle _buttonStyle;
    private bool     _stylesBuilt;

    // Lifecycle
    private void Awake()
    {
        _visible = visibleOnStart;
        Application.logMessageReceived += OnLogReceived;
    }

    private void OnDestroy()
    {
        Application.logMessageReceived -= OnLogReceived;
    }

    private void Update()
    {
        if (Input.GetKeyDown(toggleKey))
            _visible = !_visible;
    }

    // Log capture

    private void OnLogReceived(string message, string stackTrace, LogType type)
    {
        string prefix = type switch
        {
            LogType.Warning => "<color=yellow>[WARN]</color> ",
            LogType.Error   => "<color=red>[ERR]</color> ",
            LogType.Exception => "<color=red>[EXC]</color> ",
            _               => ""
        };

        _lines.Add(($"{prefix}{message}", type));

        if (_lines.Count > maxLines)
            _lines.RemoveAt(0);

        // Auto-scroll to bottom on new message
        _scroll.y = float.MaxValue;
    }

    // GUI

    private void OnGUI()
    {
        BuildStyles();

        // Small toggle button always visible in top-left corner
        if (GUI.Button(new Rect(5, 5, 80, 22), _visible ? "Console ▲" : "Console ▼", _buttonStyle))
            _visible = !_visible;

        if (!_visible) return;

        float screenW = Screen.width;
        float screenH = Screen.height;
        float panelH  = screenH * 0.4f;  // takes up 40% of screen height

        // Background panel
        GUI.Box(new Rect(0, 28, screenW, panelH), GUIContent.none, _boxStyle);

        // Clear button
        if (GUI.Button(new Rect(screenW - 60, 30, 55, 20), "Clear", _buttonStyle))
            _lines.Clear();

        // Scrollable log area
        Rect scrollRect    = new Rect(4, 52, screenW - 8, panelH - 26);
        float contentHeight = _lines.Count * (fontSize + 4) + 8;
        Rect contentRect   = new Rect(0, 0, scrollRect.width - 16, contentHeight);

        _scroll = GUI.BeginScrollView(scrollRect, _scroll, contentRect);

        float y = 4f;
        foreach (var (text, type) in _lines)
        {
            GUIStyle style = type switch
            {
                LogType.Warning   => _warnStyle,
                LogType.Error     => _errorStyle,
                LogType.Exception => _errorStyle,
                _                 => _logStyle
            };

            float lineH = fontSize + 4;
            GUI.Label(new Rect(4, y, contentRect.width - 8, lineH), text, style);
            y += lineH;
        }

        GUI.EndScrollView();
    }

    // Style builder (called once)

    private void BuildStyles()
    {
        if (_stylesBuilt) return;
        _stylesBuilt = true;

        // Semi-transparent dark background
        var bgTex = new Texture2D(1, 1);
        bgTex.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.82f));
        bgTex.Apply();

        _boxStyle = new GUIStyle(GUI.skin.box)
        {
            normal = { background = bgTex }
        };

        _logStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize  = fontSize,
            wordWrap  = false,
            richText  = true,
            normal    = { textColor = new Color(0.85f, 0.85f, 0.85f) }
        };

        _warnStyle = new GUIStyle(_logStyle)
        {
            normal = { textColor = new Color(1f, 0.85f, 0.2f) }
        };

        _errorStyle = new GUIStyle(_logStyle)
        {
            normal = { textColor = new Color(1f, 0.35f, 0.35f) }
        };

        var btnTex = new Texture2D(1, 1);
        btnTex.SetPixel(0, 0, new Color(0.15f, 0.15f, 0.15f, 0.9f));
        btnTex.Apply();

        _buttonStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = 11,
            normal   = { background = btnTex, textColor = Color.white }
        };
    }
}