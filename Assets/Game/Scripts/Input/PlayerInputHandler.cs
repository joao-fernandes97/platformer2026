using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerInputHandler : MonoBehaviour
{
    // ── Exposed Input State (read by PlayerController) ──────────────────────
    public Vector2 MoveInput       { get; private set; }
    public bool    JumpPressed     { get; private set; }   // true for one frame
    public bool    JumpHeld        { get; private set; }   // true while held
    public bool    SprintHeld      { get; private set; }
    public bool    ClimbPressed    { get; private set; }   // ledge climb confirm

    // ── Internal ─────────────────────────────────────────────────────────────
    private PlayerInputActions _actions;   // generated C# class from Input Action Asset

    // ── Lifecycle ────────────────────────────────────────────────────────────
    private void Awake()
    {
        _actions = new PlayerInputActions();
    }

    private void OnEnable()
    {
        _actions.Player.Enable();

        _actions.Player.Jump.performed  += OnJumpPerformed;
        _actions.Player.Jump.canceled   += OnJumpCanceled;
        _actions.Player.Climb.performed += OnClimbPerformed;
    }

    private void OnDisable()
    {
        _actions.Player.Jump.performed  -= OnJumpPerformed;
        _actions.Player.Jump.canceled   -= OnJumpCanceled;
        _actions.Player.Climb.performed -= OnClimbPerformed;

        _actions.Player.Disable();
    }

    // ── Update ───────────────────────────────────────────────────────────────
    private void Update()
    {
        MoveInput   = _actions.Player.Move.ReadValue<Vector2>();
        SprintHeld  = _actions.Player.Sprint.IsPressed();
        JumpHeld    = _actions.Player.Jump.IsPressed();
    }

    /// <summary>
    /// Call at the END of PlayerController.Update to clear one-frame flags.
    /// </summary>
    public void ConsumeFrameInputs()
    {
        JumpPressed  = false;
        ClimbPressed = false;
    }

    // ── Callbacks ────────────────────────────────────────────────────────────
    private void OnJumpPerformed(InputAction.CallbackContext _)  => JumpPressed  = true;
    private void OnJumpCanceled(InputAction.CallbackContext _)   => JumpHeld     = false;
    private void OnClimbPerformed(InputAction.CallbackContext _) => ClimbPressed = true;
}
