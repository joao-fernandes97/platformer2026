using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using System;
using System.Linq;
using System.Threading.Tasks;
using TMPro;
using Unity.Services.Core;
using Unity.Services.Authentication;
using Unity.Services.Relay.Models;
using UnityEngine.UnityConsent;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Build.Reporting;
#endif

#if UNITY_STANDALONE_WIN
using System.Runtime.InteropServices;
using System.Diagnostics;
#endif

using Debug = UnityEngine.Debug;

/// <summary>
/// Bootstraps the networked session and spawns player prefabs on connection.
/// </summary>
public class NetworkSetup : MonoBehaviour
{
    [Header("Player Spawning")]
    [SerializeField] private GameObject playerPrefab;
    [SerializeField] private GameObject player2Prefab;

    [SerializeField] private List<Transform> playerSpawnLocations = new();

    [Header("Relay")]
    [SerializeField] private int maxPlayers = 2;

    [Header("UI")]
    [SerializeField] private LobbyUI lobbyUI;

    [Header("Analytics")]
    [SerializeField] private bool enableAnalytics;

    
    // Relay data container
    private class RelayHostData
    {
        public string JoinCode;
        public string IPv4Address;
        public ushort Port;
        public Guid   AllocationID;
        public byte[] AllocationIDBytes;
        public byte[] ConnectionData;
        public byte[] HostConnectionData;
        public byte[] Key;
    }

    // Runtime State
    private RelayHostData  _relayData;
    private UnityTransport _transport;
    private bool           _isRelay;

    // Set to true once the client successfully completes its handshake.
    // Used to distinguish a transport-level drop from a successful mid-game disconnect.
    private bool _clientFullyConnected;

    // Set to true when the Relay transport layer logs its "allocation full" message.
    private bool _relayReportedFull;

    private const string DisconnectReasonFull = "session_full";
    private const string RelayFullLogFragment  = "maximum connected players";

#region  Lifecycle

    private void Start()
    {
        _transport = GetComponent<UnityTransport>();
        _isRelay   = _transport.Protocol == UnityTransport.ProtocolType.RelayUnityTransport;

        // --server on the command line skips the lobby (editor / CI use).
        if (HasCommandLineFlag("--server"))
        {
            lobbyUI?.Hide();
            StartCoroutine(StartAsServerCR());
            return;
        }

        lobbyUI?.ShowRoleSelect();
    }

    // Called by LobbyUI buttons

    /// <summary>Called when the player clicks "Start Server" in the lobby.</summary>
    public void OnStartServerPressed()
    {
        StartCoroutine(StartAsServerCR());
    }

    /// <summary>Called when the player clicks "Join Game" with a code typed in.</summary>
    public void OnJoinPressed(string code)
    {
        StartCoroutine(StartAsClientCR(code.Trim().ToUpper()));
    }
#endregion
    
    // Server Startup
    private IEnumerator StartAsServerCR()
    {
        lobbyUI?.ShowServerStarting();
        SetWindowTitle("Starting as server...");
        yield return null;

        if (_isRelay)
        {
            yield return RunAsync(Login());
            yield return RunAsync(SetupRelayServer());
        }

        InitAnalytics();

        var networkManager = GetComponent<NetworkManager>();
        if (networkManager.StartServer())
        {
            SetWindowTitle("MR:S - Server");
            Debug.Log("[NetworkSetup] Server listening.");

            networkManager.OnClientConnectedCallback  += OnClientConnected;
            networkManager.OnClientDisconnectCallback += OnClientDisconnected;

            if (!_isRelay)
                lobbyUI?.ShowServerReady("(direct connect)", 0, maxPlayers);
        }
        else
        {
            SetWindowTitle("Server failed to start");
            lobbyUI?.ShowError("Server failed to start.");
            Debug.LogError("[NetworkSetup] Failed to start server.");
        }
    }

    // Client Startup
    private IEnumerator StartAsClientCR(string code)
    {
        lobbyUI?.ShowClientConnecting();
        SetWindowTitle("Connecting...");
        yield return null;

        if (_isRelay)
        {
            yield return RunAsync(Login());
            yield return RunAsync(JoinRelayAllocation(code));
        }

        InitAnalytics();

        _clientFullyConnected = false;
        _relayReportedFull    = false;

        // Subscribe to Unity's log stream to detect the Relay transport-level
        // "allocation full" message.
        Application.logMessageReceived += OnLogMessageReceived;

        var networkManager = GetComponent<NetworkManager>();
        if (networkManager.StartClient())
        {
            SetWindowTitle("Maze Runner: Survive");
            Debug.Log("[NetworkSetup] Client connecting...");

            networkManager.OnClientConnectedCallback  += OnLocalClientConnected;
            networkManager.OnClientDisconnectCallback += OnLocalClientDisconnected;
        }
        else
        {
            Application.logMessageReceived -= OnLogMessageReceived;
            SetWindowTitle("Connection failed");
            lobbyUI?.ShowError("Failed to connect. Check the join code and try again.");
            Debug.LogError("[NetworkSetup] Failed to start client.");
        }
    }

#region Connection Callbacks (Server)

    private void OnClientConnected(ulong clientId)
    {
        if (!NetworkManager.Singleton.IsServer) return;

        int currentCount = NetworkManager.Singleton.ConnectedClientsIds.Count;

        if (currentCount > maxPlayers)
        {
            Debug.Log($"[NetworkSetup] Session full ({maxPlayers} players). Rejecting client {clientId}.");
            NetworkManager.Singleton.DisconnectClient(clientId, DisconnectReasonFull);
            return;
        }

        Debug.Log($"[NetworkSetup] Client {clientId} connected — spawning player. ({currentCount}/{maxPlayers})");
        SpawnPlayerForClient(clientId);
        lobbyUI?.UpdatePlayerCount(currentCount, maxPlayers);
    }

    private void OnClientDisconnected(ulong clientId)
    {
        if (!NetworkManager.Singleton.IsServer) return;

        int currentCount = NetworkManager.Singleton.ConnectedClientsIds.Count;
        Debug.Log($"[NetworkSetup] Client {clientId} disconnected. ({currentCount}/{maxPlayers})");
        lobbyUI?.UpdatePlayerCount(currentCount, maxPlayers);
    }
#endregion
    
#region Connection Callbacks (Client)
    private void OnLogMessageReceived(string message, string stackTrace, LogType type)
    {
        // Catches the Relay transport-level rejection when the allocation is full.
        if (message.IndexOf(RelayFullLogFragment, StringComparison.OrdinalIgnoreCase) >= 0)
            _relayReportedFull = true;
    }

    private void OnLocalClientConnected(ulong clientId)
    {
        _clientFullyConnected = true;
        Application.logMessageReceived -= OnLogMessageReceived;
        lobbyUI?.Hide();

        var nm = GetComponent<NetworkManager>();
        nm.OnClientConnectedCallback  -= OnLocalClientConnected;
        nm.OnClientDisconnectCallback -= OnLocalClientDisconnected;
    }

    private void OnLocalClientDisconnected(ulong clientId)
    {
        Application.logMessageReceived -= OnLogMessageReceived;

        var nm = GetComponent<NetworkManager>();
        nm.OnClientConnectedCallback  -= OnLocalClientConnected;
        nm.OnClientDisconnectCallback -= OnLocalClientDisconnected;

        // Check NGO-level reason first (set when server calls DisconnectClient
        // with a reason string), then fall back to the transport-level log flag.
        string reason = NetworkManager.Singleton.DisconnectReason;
        bool isFull   = reason == DisconnectReasonFull || _relayReportedFull;

        if (isFull)
        {
            lobbyUI?.ShowRoleSelect();
            lobbyUI?.ShowError("This session is already full.");
        }
        else if (!_clientFullyConnected)
        {
            lobbyUI?.ShowRoleSelect();
            lobbyUI?.ShowError("Could not connect to the session.\nCheck the join code and try again.");
        }
    }
#endregion
    
#region  Player Spawning
    private void SpawnPlayerForClient(ulong clientId)
    {
        if (playerPrefab == null)
        {
            Debug.LogError("[NetworkSetup] playerPrefab is not assigned!");
            return;
        }

        Vector3 spawnPos = PickSpawnPosition();

        int currentCount = NetworkManager.Singleton.ConnectedClientsIds.Count;
        GameObject prefabToSpawn = (currentCount >= 2 && player2Prefab != null)
            ? player2Prefab
            : playerPrefab;
        var go = Instantiate(prefabToSpawn, spawnPos, Quaternion.identity);
        var netObj = go.GetComponent<NetworkObject>();
        if (netObj == null)
        {
            Debug.LogError("[NetworkSetup] playerPrefab has no NetworkObject component!");
            Destroy(go);
            return;
        }

        netObj.SpawnAsPlayerObject(clientId, destroyWithScene: true);

        var pc = go.GetComponent<PlayerController>();
        if (pc != null)
        {
            CheckpointManager.Instance?.TrackPlayer(pc);               
        }
            

        Debug.Log($"[NetworkSetup] Spawned player for client {clientId} at {spawnPos}");
    }

    private Vector3 PickSpawnPosition()
    {
        if (CheckpointManager.Instance?.ActiveCheckpoint != null)
            return CheckpointManager.Instance.ActiveCheckpoint.SpawnPosition;

        var activePlayers = FindObjectsByType<PlayerController>(FindObjectsSortMode.None);

        // Find the spawn point furthest from all active players.
        // This handles both the "free point" case and the fallback gracefully.
        Vector3 bestPos      = playerSpawnLocations.Count > 0
            ? playerSpawnLocations[0].position
            : Vector3.zero;
        float   bestDist     = float.MinValue;

        foreach (var spawnPoint in playerSpawnLocations)
        {
            // If no players exist yet, just take the first point.
            if (activePlayers.Length == 0)
                return spawnPoint.position;

            float closestDist = float.MaxValue;
            foreach (var player in activePlayers)
                closestDist = Mathf.Min(closestDist,
                    Vector3.Distance(player.transform.position, spawnPoint.position));

            // Prefer the point with the most clearance from any player.
            if (closestDist > bestDist)
            {
                bestDist = closestDist;
                bestPos  = spawnPoint.position;
            }
        }

        if (bestPos == Vector3.zero)
            Debug.LogWarning("[NetworkSetup] No spawn locations assigned. Spawning at world origin.");

        return bestPos;
    }
#endregion
    
#region Relay (Server)
    private async Task SetupRelayServer()
    {
        Allocation allocation = await Unity.Services.Relay.RelayService.Instance
            .CreateAllocationAsync(maxPlayers);

        _relayData = new RelayHostData();
        foreach (var endpoint in allocation.ServerEndpoints)
        {
            _relayData.IPv4Address = endpoint.Host;
            _relayData.Port        = (ushort)endpoint.Port;
            break;
        }
        _relayData.AllocationID      = allocation.AllocationId;
        _relayData.AllocationIDBytes = allocation.AllocationIdBytes;
        _relayData.ConnectionData    = allocation.ConnectionData;
        _relayData.Key               = allocation.Key;

        string code = await Unity.Services.Relay.RelayService.Instance
            .GetJoinCodeAsync(_relayData.AllocationID);
        _relayData.JoinCode = code;

        _transport.SetRelayServerData(
            _relayData.IPv4Address, _relayData.Port,
            _relayData.AllocationIDBytes, _relayData.Key,
            _relayData.ConnectionData);

        lobbyUI?.ShowServerReady(code, 0, maxPlayers);
        Debug.Log($"[NetworkSetup] Relay ready — join code: {code}");
    }
#endregion
    
#region Relay (Client)
    private async Task JoinRelayAllocation(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            Debug.LogError("[NetworkSetup] No join code provided.");
            return;
        }

        JoinAllocation allocation = await Unity.Services.Relay.RelayService.Instance
            .JoinAllocationAsync(code);

        _relayData = new RelayHostData();
        foreach (var endpoint in allocation.ServerEndpoints)
        {
            _relayData.IPv4Address = endpoint.Host;
            _relayData.Port        = (ushort)endpoint.Port;
            break;
        }
        _relayData.AllocationIDBytes  = allocation.AllocationIdBytes;
        _relayData.ConnectionData     = allocation.ConnectionData;
        _relayData.HostConnectionData = allocation.HostConnectionData;
        _relayData.Key                = allocation.Key;

        _transport.SetRelayServerData(
            _relayData.IPv4Address, _relayData.Port,
            _relayData.AllocationIDBytes, _relayData.Key,
            _relayData.ConnectionData, _relayData.HostConnectionData);

        Debug.Log($"[NetworkSetup] Joined Relay allocation for code: {code}");
    }
#endregion
    
#region Unity Services
    
    private static async Task Login()
    {
        await UnityServices.InitializeAsync();
        if (!AuthenticationService.Instance.IsSignedIn)
            await AuthenticationService.Instance.SignInAnonymouslyAsync();
        Debug.Log("[NetworkSetup] Signed in anonymously.");
    }

    private void InitAnalytics()
    {
        if (!enableAnalytics) return;
        ConsentState state = EndUserConsent.GetConsentState();
        state.AnalyticsIntent = ConsentStatus.Granted;
        EndUserConsent.SetConsentState(state);
    }
#endregion
#region Helpers
    
    private static bool HasCommandLineFlag(string flag)
    {
        foreach (var arg in Environment.GetCommandLineArgs())
            if (arg == flag) return true;
        return false;
    }

    private static IEnumerator RunAsync(Task task)
    {
        while (!task.IsCompleted)
            yield return null;

        if (task.Exception != null)
            Debug.LogError("[NetworkSetup] Async error: " +
                           task.Exception.Flatten().InnerException);
    }
#endregion

#region Editor/Build Helpers

#if UNITY_STANDALONE_WIN
    [DllImport("user32.dll")] static extern bool SetWindowText(IntPtr hWnd, string text);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] static extern IntPtr EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private static void SetWindowTitle(string title)
    {
#if !UNITY_EDITOR
        uint pid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        IntPtr hWnd = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint windowPid);
            if (windowPid != pid) return true;
            hWnd = h;
            return false;
        }, IntPtr.Zero);
        if (hWnd != IntPtr.Zero) SetWindowText(hWnd, title);
#endif
    }
#else
    private static void SetWindowTitle(string title) { }
#endif

#if UNITY_EDITOR
    private const string BuildPath = "Builds\\Maze Runner Survive.exe";
    private const string ExeName   = "Maze Runner Survive";

    [MenuItem("Tools/Build Windows (x64)", priority = 0)]
    public static bool BuildGame()
    {
        var options = new BuildPlayerOptions
        {
            scenes           = EditorBuildSettings.scenes
                                   .Where(s => s.enabled)
                                   .Select(s => s.path)
                                   .ToArray(),
            locationPathName = BuildPath,
            target           = BuildTarget.StandaloneWindows64,
            options          = BuildOptions.None,
        };

        var report = BuildPipeline.BuildPlayer(options);
        Debug.Log($"[Build] Result: {report.summary.result}");
        return report.summary.result == BuildResult.Succeeded;
    }

    [MenuItem("Tools/Build and Launch (Server + Client)", priority = 10)]
    public static void BuildAndLaunchBoth()
    {
        CloseAll();
        if (BuildGame()) LaunchBoth();
    }

    [MenuItem("Tools/Build and Launch (Server)", priority = 15)]
    public static void BuildAndLaunchServer()
    {
        CloseAll();
        if (BuildGame()) LaunchServer();
    }

    [MenuItem("Tools/Build and Launch (Client)", priority = 20)]
    public static void BuildAndLaunchClient()
    {
        CloseAll();
        if (BuildGame()) LaunchClient();
    }

    [MenuItem("Tools/Launch (Server + Client) _F11", priority = 30)]
    public static void LaunchBoth()
    {
        LaunchServer();
        LaunchClient();
    }

    [MenuItem("Tools/Launch (Server)", priority = 35)]
    public static void LaunchServer() => Run(BuildPath, "--server");

    [MenuItem("Tools/Launch (Client)", priority = 40)]
    public static void LaunchClient() => Run(BuildPath, "");

    [MenuItem("Tools/Close All", priority = 100)]
    public static void CloseAll()
    {
        foreach (var p in System.Diagnostics.Process.GetProcessesByName(ExeName))
        {
            try   { p.Kill(); p.WaitForExit(); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Build] Could not kill {p.ProcessName}: {ex.Message}");
            }
        }
    }

    private static void Run(string path, string args)
    {
        var p = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName               = path,
                Arguments              = args,
                WindowStyle            = System.Diagnostics.ProcessWindowStyle.Normal,
                UseShellExecute        = true,
                RedirectStandardOutput = false,
            }
        };
        p.Start();
    }
#endif
#endregion
}