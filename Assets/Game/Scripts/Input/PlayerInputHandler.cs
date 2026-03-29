using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(PlayerInput))]
public class PlayerInputHandler : MonoBehaviour
{
    public Vector2 MoveInput    { get; private set; }
    public bool    JumpPressed  { get; private set; }
    public bool    JumpHeld     { get; private set; }
    public bool    SprintHeld   { get; private set; }
    public bool    ClimbPressed { get; private set; }
    public bool    InteractPressed { get; private set; }

    private InputAction _moveAction;
    private InputAction _jumpAction;
    private InputAction _sprintAction;
    private InputAction _climbAction;
    private InputAction _interactAction;

    private void Start()
    {
        var playerInput = GetComponent<PlayerInput>();

        // Explicitly pair the keyboard to this PlayerInput using whichever
        // Default Scheme you set in the Inspector ("WASD" or "Arrows").
        // This is what populates the devices list and activates the binding mask.
        playerInput.SwitchCurrentControlScheme(
            playerInput.defaultControlScheme,
            Keyboard.current
        );

        _moveAction   = playerInput.actions["Move"];
        _jumpAction   = playerInput.actions["Jump"];
        _sprintAction = playerInput.actions["Sprint"];
        _climbAction  = playerInput.actions["Climb"];
        _interactAction = playerInput.actions["Interact"];

        _jumpAction.performed  += OnJumpPerformed;
        _jumpAction.canceled   += OnJumpCanceled;
        _climbAction.performed += OnClimbPerformed;
        _interactAction.performed += OnInteractPerformed;
    }

    private void OnDestroy()
    {
        if (_jumpAction == null) return;
        _jumpAction.performed  -= OnJumpPerformed;
        _jumpAction.canceled   -= OnJumpCanceled;
        _climbAction.performed -= OnClimbPerformed;
        _interactAction.performed -= OnInteractPerformed;
    }

    private void Update()
    {
        if (_moveAction == null) return;
        MoveInput  = _moveAction.ReadValue<Vector2>();
        SprintHeld = _sprintAction.IsPressed();
        JumpHeld   = _jumpAction.IsPressed();
    }

    private void LateUpdate()
    {
        InteractPressed = false;
    }

    public void ConsumeFrameInputs()
    {
        JumpPressed  = false;
        ClimbPressed = false;
    }

    private void OnJumpPerformed(InputAction.CallbackContext _)  => JumpPressed  = true;
    private void OnJumpCanceled(InputAction.CallbackContext _)   => JumpHeld     = false;
    private void OnClimbPerformed(InputAction.CallbackContext _) => ClimbPressed = true;
    private void OnInteractPerformed(InputAction.CallbackContext _) => InteractPressed = true;
}