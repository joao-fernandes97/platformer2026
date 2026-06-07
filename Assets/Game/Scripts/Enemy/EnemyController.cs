using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Chasing enemy with line-of-sight detection and "memory".
/// </summary>
[RequireComponent(typeof(Rigidbody2D))]
[RequireComponent(typeof(HealthComponent))]
[RequireComponent(typeof(ContactDamage))]
public class EnemyController : NetworkBehaviour
{
    [Header("Detection")]
    public float aggroRadius = 8f;
    public LayerMask losBlockingLayers;
    public float eyeHeight = 1f;
    public float targetUpdateInterval = 0.5f;

    [Header("Movement")]
    public float moveSpeed    = 4f;
    public float acceleration = 40f;
    public float deceleration = 60f;

    [Header("Investigate")]
    public float investigateDuration = 5f;

    [Header("Death")]
    public float despawnDelay = 1.5f;

    // States
    private enum EnemyState { Idle, Chase, Investigate }

    private EnemyState _state = EnemyState.Idle;

    // Components
    private Rigidbody2D     _rb;
    private HealthComponent _health;
    private Animator        _animator;   // optional

    // Runtime State
    private PlayerController _target;
    private float            _targetUpdateTimer;
    private float            _damageCooldownTimer;
    private float            _facingSign = 1f;

    // Investigation memory
    private float   _lastKnownDirectionX;   // sign at moment of contact loss
    private float   _investigateTimer;

    // Spawn snapshot. Recorded once on the server, used by ResetToSpawn
    private Vector3 _spawnPosition;
    private float   _spawnFacingSign = 1f;

#region Lifecycle
    private void Awake()
    {
        _rb       = GetComponent<Rigidbody2D>();
        _health   = GetComponent<HealthComponent>();
        _animator = GetComponent<Animator>();

        _rb.freezeRotation = true;
        _health.OnDied += OnDied;
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        if (!IsServer) return;

        // Snapshot spawn state so ResetToSpawn can restore it.
        _spawnPosition   = transform.position;
        _spawnFacingSign = transform.localScale.x >= 0f ? 1f : -1f;
    }
    
    public override void OnDestroy()
    {
        base.OnDestroy();
        _health.OnDied -= OnDied;
    }

    private void FixedUpdate()
    {
        if (!IsServer) return;
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
#endregion
    
#region Target Tracking
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
            // Clear sightline, start or continue chasing
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
            // Never had a target and nothing in LOS, stay idle
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
#endregion
    
#region States
    
    private void TickIdle()
    {
        float newVelX = Mathf.MoveTowards(
            _rb.linearVelocityX, 0f, deceleration * Time.fixedDeltaTime);
        _rb.linearVelocity = new Vector2(newVelX, _rb.linearVelocityY);
    }

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
#endregion
    
    // Shared Movement

    private void MoveToward(Vector2 destination)
    {
        float dirX  = Mathf.Sign(destination.x - transform.position.x);
        _facingSign = dirX;

        float newVelX = Mathf.MoveTowards(
            _rb.linearVelocityX, dirX * moveSpeed, acceleration * Time.fixedDeltaTime);

        _rb.linearVelocity   = new Vector2(newVelX, _rb.linearVelocityY);
        transform.localScale = new Vector3(_facingSign, 1f, 1f);
    }

    /// <summary>
    /// Reset  (server-only, called by CheckpointManager on respawn)
    /// Teleports the enemy back to its spawn position and resets all AI state.
    /// Must be called on the server. Does nothing if the enemy is already dead
    /// (dead enemies self-destruct via Destroy and will not be in the scene).
    /// </summary>
    public void ResetToSpawn()
    {
        if (!IsServer) return;
        if (_health.IsDead) return;

        // Physics
        _rb.bodyType       = RigidbodyType2D.Dynamic;
        _rb.linearVelocity = Vector2.zero;
        _rb.gravityScale   = 1f;

        // Position
        transform.position   = _spawnPosition;
        transform.localScale = new Vector3(_spawnFacingSign, 1f, 1f);
        _facingSign          = _spawnFacingSign;

        // Collider
        if (TryGetComponent<Collider2D>(out var col))
            col.enabled = true;

        // AI state
        _target               = null;
        _targetUpdateTimer    = 0f;
        _investigateTimer     = 0f;
        _lastKnownDirectionX  = _spawnFacingSign;
        TransitionTo(EnemyState.Idle);

        // Animator
        if (_animator != null)
        {
            _animator.ResetTrigger(AnimDeath);
            _animator.Rebind();
            _animator.Update(0f);
        }
    }


    
    // Death
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

    
    // State Machine
    private void TransitionTo(EnemyState next)
    {
        if (_state == next) return;
        _state = next;
    }

    //Animator
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

    // Gizmos
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