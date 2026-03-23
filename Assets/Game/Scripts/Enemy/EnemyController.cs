using UnityEngine;

/// <summary>
/// Chasing enemy with line-of-sight detection and memory.
///
/// ═══════════════════════════════════════════════════════════
///  STATE MACHINE
/// ═══════════════════════════════════════════════════════════
///
///  Idle ──(player in radius + LOS)──► Chase
///                                        │
///                                    (LOS lost)
///                                        │
///                                        ▼
///                                   Investigate
///                                (walk toward last
///                                 known position)
///                                        │
///                               (arrived OR timeout)
///                                        │
///                                        ▼
///                                       Idle
///
///  From Investigate: player re-enters LOS → back to Chase.
///
/// ═══════════════════════════════════════════════════════════
///  LINE OF SIGHT
/// ═══════════════════════════════════════════════════════════
///  Raycast from eye height toward the player's centre.
///  If geometry is hit before the player, LOS is blocked.
///  Assign your geometry/ground layers to losBlockingLayers —
///  leave triggers and non-solid layers out of that mask.
///
///  Initial aggro requires clear LOS. Once already chasing,
///  LOS loss triggers Investigate rather than immediately
///  returning to Idle, giving the enemy short-term memory.
///
/// ═══════════════════════════════════════════════════════════
///  MULTIPLAYER MIGRATION (NGO server-authoritative)
/// ═══════════════════════════════════════════════════════════
///  1. Inherit NetworkBehaviour.
///  2. Wrap FixedUpdate with: if (!IsServer) return;
///  3. Add a NetworkTransform for client-side interpolation.
///  4. TakeDamage via ServerRpc from clients.
///  5. _target and _lastKnownPosition are server-only; no sync needed.
/// </summary>
[RequireComponent(typeof(Rigidbody2D))]
[RequireComponent(typeof(HealthComponent))]
[RequireComponent(typeof(ContactDamage))]
public class EnemyController : MonoBehaviour
{
    // ════════════════════════════════════════════════════════
    // INSPECTOR CONFIG
    // ════════════════════════════════════════════════════════

    [Header("Detection")]
    [Tooltip("Radius within which the enemy can notice players.")]
    public float aggroRadius = 8f;
    [Tooltip("Layer(s) that block line of sight (typically your geometry layer).")]
    public LayerMask losBlockingLayers;
    [Tooltip("Height above transform.position used as the eye origin for LOS raycasts.")]
    public float eyeHeight = 1f;
    [Tooltip("How often (seconds) to re-evaluate the closest target.")]
    public float targetUpdateInterval = 0.5f;

    [Header("Movement")]
    public float moveSpeed    = 4f;
    public float acceleration = 40f;
    public float deceleration = 60f;

    [Header("Investigate")]
    [Tooltip("How long the enemy keeps moving in the last known direction before giving up.")]
    public float investigateDuration = 5f;

    [Header("Death")]
    [Tooltip("Seconds before the GameObject is destroyed after dying.")]
    public float despawnDelay = 1.5f;

    // ════════════════════════════════════════════════════════
    // STATES
    // ════════════════════════════════════════════════════════

    private enum EnemyState { Idle, Chase, Investigate }

    private EnemyState _state = EnemyState.Idle;

    // ════════════════════════════════════════════════════════
    // COMPONENTS
    // ════════════════════════════════════════════════════════

    private Rigidbody2D     _rb;
    private HealthComponent _health;
    private Animator        _animator;   // optional

    // ════════════════════════════════════════════════════════
    // RUNTIME STATE
    // ════════════════════════════════════════════════════════

    private PlayerController _target;
    private float            _targetUpdateTimer;
    private float            _damageCooldownTimer;
    private float            _facingSign = 1f;

    // Investigation memory
    private float   _lastKnownDirectionX;   // sign at moment of contact loss
    private float   _investigateTimer;

    // ════════════════════════════════════════════════════════
    // LIFECYCLE
    // ════════════════════════════════════════════════════════

    private void Awake()
    {
        _rb       = GetComponent<Rigidbody2D>();
        _health   = GetComponent<HealthComponent>();
        _animator = GetComponent<Animator>();

        _rb.freezeRotation = true;
        _health.OnDied += OnDied;
    }

    private void OnDestroy()
    {
        _health.OnDied -= OnDied;
    }

    private void FixedUpdate()
    {
        if (_health.IsDead) return;

        UpdateTarget();

        switch (_state)
        {
            case EnemyState.Idle:        TickIdle();        break;
            case EnemyState.Chase:       TickChase();       break;
            case EnemyState.Investigate: TickInvestigate(); break;
        }

        UpdateAnimator();
    }

    // ════════════════════════════════════════════════════════
    // LINE OF SIGHT
    // ════════════════════════════════════════════════════════

    private bool HasLineOfSight(PlayerController player)
    {
        Vector2 eyePos    = (Vector2)transform.position + Vector2.up * eyeHeight;
        Vector2 targetPos = (Vector2)player.transform.position;
        Vector2 direction = targetPos - eyePos;
        float   distance  = direction.magnitude;

        // If anything on the blocking layers is hit before we reach the player, LOS fails
        RaycastHit2D hit = Physics2D.Raycast(eyePos, direction.normalized, distance, losBlockingLayers);
        return hit.collider == null;
    }

    // ════════════════════════════════════════════════════════
    // TARGET TRACKING
    // ════════════════════════════════════════════════════════

    private void UpdateTarget()
    {
        _targetUpdateTimer -= Time.fixedDeltaTime;
        if (_targetUpdateTimer > 0f) return;
        _targetUpdateTimer = targetUpdateInterval;

        PlayerController candidate = PlayerRegistry.Instance?.GetClosest(transform.position);

        if (candidate == null)
        {
            LoseTarget();
            return;
        }

        float dist   = Vector2.Distance(transform.position, candidate.transform.position);
        bool  hasLOS = dist <= aggroRadius && HasLineOfSight(candidate);

        if (hasLOS)
        {
            // Clear sightline — start or continue chasing
            _target = candidate;
            TransitionTo(EnemyState.Chase);
        }
        else if (_state == EnemyState.Chase)
        {
            // Lost contact (LOS blocked OR left aggro range)
            // _lastKnownDirectionX is already current from TickChase
            BeginInvestigate();
        }
        else if (_state != EnemyState.Investigate)
        {
            // Never had a target and nothing in LOS — stay idle
            LoseTarget();
        }
        // If already Investigating, let TickInvestigate drive the timeout
    }

    private void LoseTarget()
    {
        _target = null;
        TransitionTo(EnemyState.Idle);
    }

    private void BeginInvestigate()
    {
        _target           = null;
        _investigateTimer = investigateDuration;
        TransitionTo(EnemyState.Investigate);
    }

    // ════════════════════════════════════════════════════════
    // STATE: IDLE
    // ════════════════════════════════════════════════════════

    private void TickIdle()
    {
        float newVelX = Mathf.MoveTowards(
            _rb.linearVelocityX, 0f, deceleration * Time.fixedDeltaTime);
        _rb.linearVelocity = new Vector2(newVelX, _rb.linearVelocityY);
    }

    // ════════════════════════════════════════════════════════
    // STATE: CHASE
    // ════════════════════════════════════════════════════════

    private void TickChase()
    {
        if (_target == null)
        {
            TransitionTo(EnemyState.Idle);
            return;
        }

        // Keep direction fresh every frame while we have sight
        _lastKnownDirectionX = Mathf.Sign(_target.transform.position.x - transform.position.x);

        MoveToward(_target.transform.position);
    }

    // ════════════════════════════════════════════════════════
    // STATE: INVESTIGATE
    // ════════════════════════════════════════════════════════

    private void TickInvestigate()
    {
        _investigateTimer -= Time.fixedDeltaTime;

        if (_investigateTimer <= 0f)
        {
            TransitionTo(EnemyState.Idle);
            return;
        }

        // Keep moving in the direction the target was last seen
        float newVelX = Mathf.MoveTowards(
            _rb.linearVelocityX,
            _lastKnownDirectionX * moveSpeed,
            acceleration * Time.fixedDeltaTime);

        _rb.linearVelocity   = new Vector2(newVelX, _rb.linearVelocityY);
        transform.localScale = new Vector3(_lastKnownDirectionX, 1f, 1f);
    }

    // ════════════════════════════════════════════════════════
    // SHARED MOVEMENT
    // ════════════════════════════════════════════════════════

    private void MoveToward(Vector2 destination)
    {
        float dirX  = Mathf.Sign(destination.x - transform.position.x);
        _facingSign = dirX;

        float newVelX = Mathf.MoveTowards(
            _rb.linearVelocityX, dirX * moveSpeed, acceleration * Time.fixedDeltaTime);

        _rb.linearVelocity   = new Vector2(newVelX, _rb.linearVelocityY);
        transform.localScale = new Vector3(_facingSign, 1f, 1f);
    }

    // ════════════════════════════════════════════════════════
    // DEATH
    // ════════════════════════════════════════════════════════

    private void OnDied()
    {
        _rb.linearVelocity = Vector2.zero;
        _rb.bodyType       = RigidbodyType2D.Kinematic;

        if (TryGetComponent<Collider2D>(out var col))
            col.enabled = false;

        if (_animator != null)
            _animator.SetTrigger(AnimDeath);

        Destroy(gameObject, despawnDelay);
    }

    // ════════════════════════════════════════════════════════
    // STATE MACHINE
    // ════════════════════════════════════════════════════════

    private void TransitionTo(EnemyState next)
    {
        if (_state == next) return;
        _state = next;
    }

    // ════════════════════════════════════════════════════════
    // ANIMATOR BRIDGE (optional)
    // ════════════════════════════════════════════════════════

    private static readonly int AnimSpeed       = Animator.StringToHash("Speed");
    private static readonly int AnimDeath       = Animator.StringToHash("Death");
    private static readonly int AnimChase       = Animator.StringToHash("Chasing");
    private static readonly int AnimInvestigate = Animator.StringToHash("Investigating");

    private void UpdateAnimator()
    {
        if (_animator == null) return;

        _animator.SetFloat(AnimSpeed,      Mathf.Abs(_rb.linearVelocityX));
        _animator.SetBool(AnimChase,       _state == EnemyState.Chase);
        _animator.SetBool(AnimInvestigate, _state == EnemyState.Investigate);
    }

    // ════════════════════════════════════════════════════════
    // GIZMOS
    // ════════════════════════════════════════════════════════

    private void OnDrawGizmosSelected()
    {
        // Aggro radius
        Gizmos.color = _state == EnemyState.Chase ? Color.red : Color.yellow;
        Gizmos.DrawWireSphere(transform.position, aggroRadius);

        // LOS ray to current target
        if (_target != null)
        {
            Gizmos.color = Color.red;
            Gizmos.DrawLine(
                (Vector2)transform.position + Vector2.up * eyeHeight,
                _target.transform.position);
        }

        // Direction arrow during investigation
        if (_state == EnemyState.Investigate)
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawRay(transform.position, Vector2.right * _lastKnownDirectionX * 2f);
        }
    }
}