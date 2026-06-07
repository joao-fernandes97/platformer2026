using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Drives the lobby Canvas panels. 
/// </summary>
public class LobbyUI : MonoBehaviour
{
    [Header("Panels")]
    public GameObject panelRoleSelect;
    public GameObject panelServerStarting;
    public GameObject panelServerReady;
    public GameObject panelClientJoin;
    public GameObject panelConnecting;
    public GameObject panelError;

    [Header("Role Select")]
    public Button btnStartServer;
    public Button btnJoinGame;

    [Header("Server Starting")]
    public TextMeshProUGUI txtServerStartingStatus;

    [Header("Server Ready")]
    public TextMeshProUGUI txtJoinCode;
    public Button btnCopyCode;

    [Header("Client Join")]
    public TMP_InputField inputJoinCode;
    public TextMeshProUGUI txtPlayerCount;
    public Button         btnJoin;
    public Button         btnBackFromJoin;

    [Header("Connecting")]
    public TextMeshProUGUI txtConnectingStatus;

    [Header("Error")]
    public TextMeshProUGUI txtError;
    public Button          btnBackFromError;

    // Reference
    private NetworkSetup _networkSetup;

    
    // Lifecycle
    private void Awake()
    {
        _networkSetup = FindFirstObjectByType<NetworkSetup>();

        // Role Select
        if (btnStartServer != null)
            btnStartServer.onClick.AddListener(OnStartServerClicked);

        if (btnJoinGame != null)
            btnJoinGame.onClick.AddListener(OnJoinGameClicked);

        // Server Ready
        if (btnCopyCode != null)
            btnCopyCode.onClick.AddListener(OnCopyCodeClicked);

        // Client Join
        if (btnJoin != null)
            btnJoin.onClick.AddListener(OnJoinClicked);

        if (btnBackFromJoin != null)
            btnBackFromJoin.onClick.AddListener(ShowRoleSelect);

        // Error
        if (btnBackFromError != null)
            btnBackFromError.onClick.AddListener(ShowRoleSelect);

        // Also allow pressing Enter in the code field to join.
        if (inputJoinCode != null)
            inputJoinCode.onSubmit.AddListener(_ => OnJoinClicked());

        HideAllPanels();
    }

    
#region Public API
    /// <summary>Show the role-selection screen.</summary>
    public void ShowRoleSelect()
    {
        gameObject.SetActive(true);
        
        HideAllPanels();
        SetActive(panelRoleSelect, true);
    }

    /// <summary>
    /// Show the "starting server…" panel while Relay is allocating.
    /// Called by NetworkSetup at the start of StartAsServerCR.
    /// </summary>
    public void ShowServerStarting()
    {
        HideAllPanels();
        SetActive(panelServerStarting, true);

        if (txtServerStartingStatus != null)
            txtServerStartingStatus.text = "Connecting to Relay…";
    }

    /// <summary>
    /// Show the server-ready panel with the join code.
    /// Called by NetworkSetup once the Relay allocation succeeds.
    /// </summary>
    public void ShowServerReady(string code, int connected, int max)
    {
        HideAllPanels();
        SetActive(panelServerReady, true);

        if (txtJoinCode != null)
            txtJoinCode.text = code;

        UpdatePlayerCount(connected, max);
    }

    /// <summary>
    /// Refresh the player-count label on the server-ready panel.
    /// Called whenever a client connects or disconnects.
    /// </summary>
    public void UpdatePlayerCount(int connected, int max)
    {
        if (txtPlayerCount != null)
            txtPlayerCount.text = $"Players: {connected} / {max}";
    }

    /// <summary>Show the "connecting…" panel while the client joins Relay.</summary>
    public void ShowClientConnecting()
    {
        HideAllPanels();
        SetActive(panelConnecting, true);

        if (txtConnectingStatus != null)
            txtConnectingStatus.text = "Joining session…";
    }

    /// <summary>Show an error panel with a message and a back button.</summary>
    public void ShowError(string message)
    {
        HideAllPanels();
        SetActive(panelError, true);

        if (txtError != null)
            txtError.text = message;
    }

    /// <summary>Hide the entire lobby (game has started).</summary>
    public void Hide()
    {
        HideAllPanels();
        gameObject.SetActive(false);
    }
#endregion
    
#region Button Handlers

    private void OnStartServerClicked()
    {
        if (_networkSetup == null)
        {
            Debug.LogError("[LobbyUI] NetworkSetup not found in scene.");
            return;
        }
        _networkSetup.OnStartServerPressed();
    }

    private void OnJoinGameClicked()
    {
        HideAllPanels();
        SetActive(panelClientJoin, true);

        // Clear any leftover code and focus the input field.
        if (inputJoinCode != null)
        {
            inputJoinCode.text = "";
            inputJoinCode.ActivateInputField();
        }
    }

    private void OnJoinClicked()
    {
        if (_networkSetup == null)
        {
            Debug.LogError("[LobbyUI] NetworkSetup not found in scene.");
            return;
        }

        string code = inputJoinCode != null ? inputJoinCode.text : "";

        if (string.IsNullOrWhiteSpace(code))
        {
            ShowError("Please enter a join code.");
            return;
        }

        _networkSetup.OnJoinPressed(code);
    }

    private void OnCopyCodeClicked()
    {
        if (txtJoinCode != null)
            GUIUtility.systemCopyBuffer = txtJoinCode.text;
    }
#endregion
    
#region Helpers
    
    private void HideAllPanels()
    {
        SetActive(panelRoleSelect,    false);
        SetActive(panelServerStarting, false);
        SetActive(panelServerReady,   false);
        SetActive(panelClientJoin,    false);
        SetActive(panelConnecting,    false);
        SetActive(panelError,         false);
    }

    private static void SetActive(GameObject go, bool active)
    {
        if (go != null) go.SetActive(active);
    }
#endregion
}