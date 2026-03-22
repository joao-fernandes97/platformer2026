using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Lightweight registry of active PlayerControllers in the scene.
/// Enemies query this instead of calling FindObjectsByType every frame.
///
/// Players register themselves in OnEnable / deregister in OnDisable,
/// so the list is always current even as players spawn and despawn.
///
/// MULTIPLAYER NOTE:
///   In NGO server-auth, only the server needs to run enemy AI, so this
///   registry only needs to be accurate on the server. No changes required —
///   NetworkObjects still call OnEnable/OnDisable on the server, so
///   registration happens automatically.
/// </summary>
public class PlayerRegistry : MonoBehaviour
{
    public static PlayerRegistry Instance { get; private set; }

    private readonly List<PlayerController> _players = new();

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    private void Start()
    {
        // Safety net: if any PlayerController's OnEnable fired before our
        // Awake set Instance (possible when everything loads in the same frame),
        // their registration was silently dropped. Scan the scene once to
        // catch any that slipped through.
        foreach (var pc in FindObjectsByType<PlayerController>(FindObjectsSortMode.None))
            Register(pc);
    }

    // ── Registration ──────────────────────────────────────────────────────────

    public void Register(PlayerController player)
    {
        if (!_players.Contains(player))
        {
            _players.Add(player);
            Debug.Log($"[Registry] Registered {player.name} — total: {_players.Count}");
        }
        else
        {
            Debug.LogWarning($"[Registry] {player.name} tried to register but was already in list.");
        }
    }

    public void Deregister(PlayerController player)
    {
        _players.Remove(player);
    }

    // ── Queries ───────────────────────────────────────────────────────────────

    /// <summary>Returns the living player closest to worldPos, or null if none.</summary>
    public PlayerController GetClosest(Vector2 worldPos)
    {
        PlayerController closest  = null;
        float            bestDist = float.MaxValue;

        foreach (var player in _players)
        {
            if (player == null || player.GetComponent<HealthComponent>().IsDead) continue;

            float dist = ((Vector2)player.transform.position - worldPos).sqrMagnitude;
            if (dist < bestDist)
            {
                bestDist = dist;
                closest  = player;
            }
        }

        return closest;
    }

    /// <summary>Returns all living players within radius.</summary>
    public List<PlayerController> GetWithinRadius(Vector2 worldPos, float radius)
    {
        var results    = new List<PlayerController>();
        float radiusSq = radius * radius;

        foreach (var player in _players)
        {
            if (player == null || player.GetComponent<HealthComponent>().IsDead) continue;

            if (((Vector2)player.transform.position - worldPos).sqrMagnitude <= radiusSq)
                results.Add(player);
        }

        return results;
    }

    /// <summary>Returns all currently living players. Used by the camera system.</summary>
    public List<PlayerController> GetAllLiving()
    {
        var results = new List<PlayerController>();

        foreach (var player in _players)
        {
            if (player == null || player.GetComponent<HealthComponent>().IsDead) continue;
            results.Add(player);
        }
        return results;
    }
}