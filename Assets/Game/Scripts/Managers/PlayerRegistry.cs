using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Lightweight registry of active PlayerControllers in the scene.
/// Enemies query this instead of calling FindObjectsByType every frame.
/// </summary>
public class PlayerRegistry : MonoBehaviour
{
    public static PlayerRegistry Instance { get; private set; }

    private readonly Dictionary<PlayerController, HealthComponent> _players = new();
    private List<(PlayerController, HealthComponent)> _snapshot = new();
    private bool _snapshotDirty = true;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
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

    // Registration
    public void Register(PlayerController player)
    {
        if (_players.ContainsKey(player)) 
        {
            Debug.LogWarning($"[Registry] {player.name} tried to register but was already in list.");
            return;
        }

        var health = player.GetComponent<HealthComponent>();
        if (health == null)
        {
            Debug.LogWarning($"[Registry] {player.name} has no HealthComponent — not registered.");
            return;
        }

        _players[player] = health;
        _snapshotDirty = true;
        Debug.Log($"[Registry] Registered {player.name} — total: {_players.Count}");
    }

    public void Deregister(PlayerController player)
    {
        if(_players.Remove(player))
            _snapshotDirty = true;
    }

    //Snapshot
    private List<(PlayerController, HealthComponent)> GetSnapshot()
    {
        if (_snapshotDirty)
        {
            // Take a point-in-time copy of the keys so mid-rebuild Deregister
            // calls don't invalidate the iteration.
            var entries = new List<KeyValuePair<PlayerController, HealthComponent>>(_players);
            var fresh   = new List<(PlayerController, HealthComponent)>(entries.Count);
            foreach (var kvp in entries)
                fresh.Add((kvp.Key, kvp.Value));
            _snapshot      = fresh;
            _snapshotDirty = false;
        }
        return _snapshot;
    }

    //Queries
    /// <summary>Returns the living player closest to worldPos, or null if none.</summary>
    public PlayerController GetClosest(Vector2 worldPos)
    {
        PlayerController closest  = null;
        float            bestDist = float.MaxValue;

        foreach (var (player, health) in GetSnapshot())
        {
            if (player == null || health.IsDead) continue;

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
        List<PlayerController> results    = new();
        float radiusSq = radius * radius;

        foreach (var (player, health) in GetSnapshot())
        {
            if (player == null || health.IsDead) continue;

            if (((Vector2)player.transform.position - worldPos).sqrMagnitude <= radiusSq)
                results.Add(player);
        }

        return results;
    }

    /// <summary>Returns all currently living players. Used by the camera system.</summary>
    public List<PlayerController> GetAllLiving()
    {
        List<PlayerController> results = new();

        foreach (var (player, health) in GetSnapshot())
        {
            if (player == null || health.IsDead) continue;
            results.Add(player);
        }
        return results;
    }
}