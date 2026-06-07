using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Moves a platform (or any GameObject) along a set of waypoints.
/// </summary>
public class PlatformMover : NetworkBehaviour
{
    public enum LoopMode { PingPong, Loop, Once }

    [Header("Waypoints")]
    public Vector2[] waypoints = { Vector2.zero, new Vector2(4f, 0f) };

    [Header("Movement")]
    public LoopMode loopMode  = LoopMode.PingPong;
    public float    moveSpeed = 3f;
    [Tooltip("Optional per-segment speed override. Index N = speed when moving TOWARD waypoint N. " +
         "Leave empty to use moveSpeed for all segments. Indices with no entry fall back to moveSpeed.")]
    public float[] segmentSpeeds;

    public float initialDelay = 0f;
    public float waypointPause = 0f;

    [Tooltip("Easing applied when approaching a waypoint (0 = linear, 1 = full ease).")]
    [Range(0f, 1f)]
    public float easing = 0.3f;

    [Header("Passengers")]
    public LayerMask passengerLayers;

    private Vector2[] _worldWaypoints;
    private int       _currentIndex  = 0;
    private int       _direction     = 1;    // +1 forward, -1 reverse (PingPong)
    private float     _pauseTimer    = 0f;
    private bool      _initialDelayDone = false;
    private bool      _stopped       = false;

    // Passenger tracking
    private List<PlayerController> _passengerList = new();

#region Lifecycle

    private void Start()
    {
        // Bake local offsets into world positions relative to start
        _worldWaypoints = new Vector2[waypoints.Length];
        for (int i = 0; i < waypoints.Length; i++)
            _worldWaypoints[i] = (Vector2)transform.position + waypoints[i];
    }

    private void FixedUpdate()
    {
        if(!IsServer) return;

        if (_stopped || waypoints.Length < 2) return;

        if (!_initialDelayDone)
        {
            initialDelay -= Time.fixedDeltaTime;
            if (initialDelay <= 0f)
                _initialDelayDone = true;
            return;
        }

        if (_pauseTimer > 0f)
        {
            _pauseTimer -= Time.fixedDeltaTime;
            return;
        }

        Vector2 target   = _worldWaypoints[_currentIndex];
        Vector2 current  = transform.position;
        float   distance = Vector2.Distance(current, target);
        float   speed    = GetCurrentSpeed(_currentIndex);
        float   step     = speed * Time.fixedDeltaTime;

        // Ease in to waypoint
        if (easing > 0f && distance < speed)
            step *= Mathf.Lerp(1f, distance / speed, easing);

        Vector2 nextPos;

        if (step >= distance)
        {
            // Arrived at waypoint
            nextPos = target;
            OnWaypointReached();
        }
        else
        {
            nextPos = Vector2.MoveTowards(current, target, step);
        }

        Vector2 delta = nextPos - current;
        if (_passengerList != null && _passengerList.Count!=0 && delta != Vector2.zero)
            foreach(var passenger in _passengerList)
            {
                CarryPassengerClientRpc(
                    passenger.GetComponent<NetworkObject>().NetworkObjectId, delta);
            }
            
        transform.position = new Vector3(nextPos.x, nextPos.y, transform.position.z);
    }
#endregion
    
    //Waypoint Logic
    private void OnWaypointReached()
    {
        if (waypointPause > 0f)
            _pauseTimer = waypointPause;

        int nextIndex = _currentIndex + _direction;

        switch (loopMode)
        {
            case LoopMode.PingPong:
                if (nextIndex >= _worldWaypoints.Length || nextIndex < 0)
                {
                    _direction  *= -1;
                    nextIndex    = _currentIndex + _direction;
                }
                _currentIndex = nextIndex;
                break;

            case LoopMode.Loop:
                _currentIndex = nextIndex % _worldWaypoints.Length;
                break;

            case LoopMode.Once:
                if (nextIndex >= _worldWaypoints.Length)
                {
                    _stopped = true;
                    return;
                }
                _currentIndex = nextIndex;
                break;
        }
    }

    // PASSENGER CARRYING
    private void OnCollisionEnter2D(Collision2D col)
    {
        if(!IsServer) return;
        
        Debug.Log($"Collision Entered {col.gameObject.name}");
        if (!IsInLayerMask(col.gameObject.layer, passengerLayers)) return;

        // Only carry if the passenger is landing on top
        if (col.contacts[0].normal.y < 0.5f) return;
        Debug.Log("Parenting");
        PlayerController passenger = col.gameObject.GetComponentInParent<PlayerController>();
        if(passenger != null)
            _passengerList.Add(passenger);
    }

    private void OnCollisionExit2D(Collision2D col)
    {
        if(!IsServer) return;

        PlayerController passenger = col.gameObject.GetComponentInParent<PlayerController>();
        if(passenger != null && _passengerList.Contains(passenger))
        {
            _passengerList.Remove(passenger);
        }
    }

    [ClientRpc]
    private void CarryPassengerClientRpc(ulong networkObjectId, Vector2 delta)
    {
        if (!NetworkManager.Singleton.SpawnManager.SpawnedObjects
                .TryGetValue(networkObjectId, out var netObj)) return;

        // Only the owning client moves their own player
        if (!netObj.IsOwner) return;

        var rb = netObj.GetComponent<Rigidbody2D>();
        if (rb != null)
            rb.position += delta;
    }

    private bool IsInLayerMask(int layer, LayerMask mask) =>
        (mask.value & (1 << layer)) != 0;

    
    //PUBLIC API
    public void SetPaused(bool paused) => _stopped = paused;
    public void ResetToStart()
    {
        _currentIndex      = 0;
        _direction         = 1;
        _stopped           = false;
        _initialDelayDone  = false;
        transform.position = new Vector3(
            _worldWaypoints[0].x, _worldWaypoints[0].y, transform.position.z);
    }

#region Helpers
    /// <summary>
    /// Returns the speed to use when traveling toward <paramref name="targetIndex"/>.
    /// Falls back to the global moveSpeed if no override is defined for that index.
    /// </summary>
    private float GetCurrentSpeed(int targetIndex)
    {
        if (segmentSpeeds != null && targetIndex < segmentSpeeds.Length && segmentSpeeds[targetIndex] > 0f)
            return segmentSpeeds[targetIndex];
        return moveSpeed;
    }
    
    private void OnDrawGizmosSelected()
    {
        if (waypoints == null || waypoints.Length == 0) return;

        Vector2 origin = Application.isPlaying
            ? _worldWaypoints[0]
            : (Vector2)transform.position;   // preview from current pos in editor

        // Draw path
        for (int i = 0; i < waypoints.Length; i++)
        {
            Vector2 wp = Application.isPlaying
                ? _worldWaypoints[i]
                : (Vector2)transform.position + waypoints[i];

            Gizmos.color = (i == _currentIndex) ? Color.green : Color.white;
            Gizmos.DrawSphere(wp, 0.15f);

            if (i < waypoints.Length - 1)
            {
                Vector2 next = Application.isPlaying
                    ? _worldWaypoints[i + 1]
                    : (Vector2)transform.position + waypoints[i + 1];

                Gizmos.color = Color.white;
                Gizmos.DrawLine(wp, next);
            }
        }

        // Draw loop connection for Loop mode
        if (loopMode == LoopMode.Loop && waypoints.Length > 1)
        {
            Vector2 first = Application.isPlaying
                ? _worldWaypoints[0]
                : (Vector2)transform.position + waypoints[0];
            Vector2 last = Application.isPlaying
                ? _worldWaypoints[waypoints.Length - 1]
                : (Vector2)transform.position + waypoints[waypoints.Length - 1];

            Gizmos.color = Color.gray;
            Gizmos.DrawLine(last, first);
        }
    }
#endregion
}