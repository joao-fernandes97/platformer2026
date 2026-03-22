using UnityEngine;

/// <summary>
/// Moves a platform (or any GameObject) along a set of waypoints.
///
/// Movement styles:
///   Loop      — reaches the last waypoint then jumps back to the first
///   PingPong  — reverses direction at each end
///   Once      — stops at the final waypoint
///
/// Waypoints are defined as local offsets from the platform's starting
/// position, so the whole path moves with the object if you reposition it.
///
/// Carries rigidbody-based characters automatically: any Rigidbody2D
/// sitting on the platform is moved with it by parenting during contact.
/// This is the simplest reliable approach for 2D; for more complex cases
/// (wall-riding, ceiling) track contacts manually.
///
/// MULTIPLAYER NOTE (NGO server-auth):
///   1. Inherit NetworkBehaviour.
///   2. Wrap FixedUpdate with: if (!IsServer) return;
///   3. Add a NetworkTransform for client interpolation.
///   4. Passenger parenting is client-side visual only — server drives position.
/// </summary>
public class PlatformMover : MonoBehaviour
{
    // ════════════════════════════════════════════════════════
    // INSPECTOR CONFIG
    // ════════════════════════════════════════════════════════

    public enum LoopMode { PingPong, Loop, Once }

    [Header("Waypoints")]
    [Tooltip("Positions relative to the platform's starting position.")]
    public Vector2[] waypoints = { Vector2.zero, new Vector2(4f, 0f) };

    [Header("Movement")]
    public LoopMode loopMode  = LoopMode.PingPong;
    public float    moveSpeed = 3f;

    [Tooltip("Seconds to wait at each waypoint before moving on.")]
    public float waypointPause = 0f;

    [Tooltip("Easing applied when approaching a waypoint (0 = linear, 1 = full ease).")]
    [Range(0f, 1f)]
    public float easing = 0.3f;

    [Header("Passengers")]
    [Tooltip("Layer(s) whose Rigidbody2D objects should be carried by this platform.")]
    public LayerMask passengerLayers;

    // ════════════════════════════════════════════════════════
    // RUNTIME STATE
    // ════════════════════════════════════════════════════════

    private Vector2[] _worldWaypoints;
    private int       _currentIndex  = 0;
    private int       _direction     = 1;    // +1 forward, -1 reverse (PingPong)
    private float     _pauseTimer    = 0f;
    private bool      _stopped       = false;

    // Passenger tracking
    private Transform _passenger;
    private Vector3   _passengerPrevParent;

    // ════════════════════════════════════════════════════════
    // LIFECYCLE
    // ════════════════════════════════════════════════════════

    private void Start()
    {
        // Bake local offsets into world positions relative to start
        _worldWaypoints = new Vector2[waypoints.Length];
        for (int i = 0; i < waypoints.Length; i++)
            _worldWaypoints[i] = (Vector2)transform.position + waypoints[i];
    }

    private void FixedUpdate()
    {
        if (_stopped || waypoints.Length < 2) return;

        if (_pauseTimer > 0f)
        {
            _pauseTimer -= Time.fixedDeltaTime;
            return;
        }

        Vector2 target   = _worldWaypoints[_currentIndex];
        Vector2 current  = transform.position;
        float   distance = Vector2.Distance(current, target);
        float   step     = moveSpeed * Time.fixedDeltaTime;

        // Ease in to waypoint
        if (easing > 0f && distance < moveSpeed)
            step *= Mathf.Lerp(1f, distance / moveSpeed, easing);

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
        MovePassenger(delta);
        transform.position = new Vector3(nextPos.x, nextPos.y, transform.position.z);
    }

    // ════════════════════════════════════════════════════════
    // WAYPOINT LOGIC
    // ════════════════════════════════════════════════════════

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

    // ════════════════════════════════════════════════════════
    // PASSENGER CARRYING
    // ════════════════════════════════════════════════════════

    private void OnCollisionEnter2D(Collision2D col)
    {
        Debug.Log($"Collision Entered {col.gameObject.name}");
        if (!IsInLayerMask(col.gameObject.layer, passengerLayers)) return;

        // Only carry if the passenger is landing on top
        if (col.contacts[0].normal.y > 0.5f) return;
        Debug.Log("Parenting");
        _passenger = col.transform;
        _passenger.SetParent(transform);
    }

    private void OnCollisionExit2D(Collision2D col)
    {
        if (col.transform == _passenger)
        {
            _passenger.SetParent(null);
            _passenger = null;
        }
    }

    private void MovePassenger(Vector2 delta)
    {
        // Parenting handles it — this is here for subclasses or non-parenting approaches
    }

    private bool IsInLayerMask(int layer, LayerMask mask) =>
        (mask.value & (1 << layer)) != 0;

    // ════════════════════════════════════════════════════════
    // PUBLIC API
    // ════════════════════════════════════════════════════════

    public void SetPaused(bool paused) => _stopped = paused;
    public void ResetToStart()
    {
        _currentIndex     = 0;
        _direction        = 1;
        _stopped          = false;
        transform.position = new Vector3(
            _worldWaypoints[0].x, _worldWaypoints[0].y, transform.position.z);
    }

    // ════════════════════════════════════════════════════════
    // GIZMOS
    // ════════════════════════════════════════════════════════

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
}