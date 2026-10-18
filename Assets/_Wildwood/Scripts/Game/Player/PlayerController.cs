using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using Wildwood.Game.UI;

namespace Wildwood.Game.Player
{
    /// <summary>
    /// Third-person movement for a networked player.
    /// Each player moves their own character on their own machine (owner-authoritative),
    /// and the NetworkTransform on the same object (set to Owner mode) copies it to everyone else.
    /// That keeps movement instant for the player, which is what we want for friends-only co-op.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class PlayerController : NetworkBehaviour
    {
        [Header("Movement")]
        [SerializeField] float walkSpeed = 4.5f;
        [SerializeField] float sprintSpeed = 7.5f;
        [Tooltip("How quickly the player reaches full speed, in m/s per second.")]
        [SerializeField] float acceleration = 40f;
        [Tooltip("How fast the body turns to face the direction of travel, in degrees per second.")]
        [SerializeField] float turnSpeed = 720f;
        [SerializeField] float jumpHeight = 1.2f;
        [SerializeField] float gravity = -20f;
        [Tooltip("Falling below this height puts the player back at their spawn point.")]
        [SerializeField] float fallResetHeight = -50f;

        [Header("Camera")]
        [SerializeField] Transform cameraTarget;
        [Tooltip("Degrees of rotation per pixel of mouse movement.")]
        [SerializeField] float mouseSensitivity = 0.1f;
        [Tooltip("Degrees per second at full stick deflection.")]
        [SerializeField] float gamepadLookSpeed = 180f;
        [SerializeField] float minPitch = -60f;
        [SerializeField] float maxPitch = 75f;

        [Header("Appearance")]
        [SerializeField] Renderer bodyRenderer;

        static readonly Color[] PlayerColors =
        {
            new Color(0.93f, 0.62f, 0.15f), // amber
            new Color(0.36f, 0.79f, 0.65f), // teal
            new Color(0.85f, 0.35f, 0.19f), // coral
            new Color(0.50f, 0.47f, 0.87f), // purple
            new Color(0.83f, 0.33f, 0.49f), // pink
            new Color(0.39f, 0.60f, 0.13f), // green
        };

        CharacterController _controller;
        InputAction _move, _look, _jump, _sprint;
        Vector3 _horizontalVelocity;
        float _verticalVelocity;
        float _yaw;
        float _pitch = 10f;

        void Awake()
        {
            _controller = GetComponent<CharacterController>();
        }

        public override void OnNetworkSpawn()
        {
            ApplyColor();
            if (!IsOwner) return; // Other players' characters are moved by their NetworkTransform.

            BindInput();
            TeleportTo(SpawnPosition(), Quaternion.identity);
            _yaw = transform.eulerAngles.y;
            ApplyCameraTarget();
            if (PlayerCameraRig.Instance != null) PlayerCameraRig.Instance.Follow(cameraTarget);
            SetCursorLocked(true);
        }

        public override void OnNetworkDespawn()
        {
            if (!IsOwner) return;
            if (PlayerCameraRig.Instance != null) PlayerCameraRig.Instance.Release(cameraTarget);
            SetCursorLocked(false);
        }

        void Update()
        {
            if (!IsSpawned || !IsOwner) return;

            UpdateCursorLock();
            bool hasControl = Cursor.lockState == CursorLockMode.Locked;

            ReadLook(hasControl);
            Move(hasControl);
            ApplyCameraTarget(); // after Move, because turning the body would otherwise drag the camera target with it

            if (transform.position.y < fallResetHeight)
                TeleportTo(SpawnPosition(), transform.rotation);
        }

        void BindInput()
        {
            // Uses the project-wide actions (Assets/InputSystem_Actions.inputactions),
            // assigned in Project Settings > Input System Package.
            var actions = InputSystem.actions;
            if (actions == null)
            {
                Debug.LogError("[Wildwood] No project-wide input actions are assigned (Project Settings > Input System Package).");
                return;
            }
            _move = actions.FindAction("Player/Move");
            _look = actions.FindAction("Player/Look");
            _jump = actions.FindAction("Player/Jump");
            _sprint = actions.FindAction("Player/Sprint");
            actions.FindActionMap("Player")?.Enable();
        }

        void ReadLook(bool hasControl)
        {
            if (!hasControl || _look == null) return;

            Vector2 delta = _look.ReadValue<Vector2>();
            bool fromGamepad = _look.activeControl?.device is Gamepad;
            float scale = fromGamepad ? gamepadLookSpeed * Time.deltaTime : mouseSensitivity;

            _yaw += delta.x * scale;
            _pitch = Mathf.Clamp(_pitch - delta.y * scale, minPitch, maxPitch);
        }

        void Move(bool hasControl)
        {
            float dt = Time.deltaTime;

            Vector2 input = hasControl && _move != null ? _move.ReadValue<Vector2>() : Vector2.zero;
            Vector3 wish = Quaternion.Euler(0f, _yaw, 0f) * new Vector3(input.x, 0f, input.y);
            if (wish.sqrMagnitude > 1f) wish.Normalize();

            bool sprinting = hasControl && _sprint != null && _sprint.IsPressed();
            Vector3 targetVelocity = wish * (sprinting ? sprintSpeed : walkSpeed);
            _horizontalVelocity = Vector3.MoveTowards(_horizontalVelocity, targetVelocity, acceleration * dt);

            if (_controller.isGrounded)
            {
                if (_verticalVelocity < 0f) _verticalVelocity = -2f; // keeps the character pressed onto the ground
                if (hasControl && _jump != null && _jump.WasPressedThisFrame())
                    _verticalVelocity = Mathf.Sqrt(2f * jumpHeight * -gravity);
            }
            _verticalVelocity += gravity * dt;

            CollisionFlags hits = _controller.Move((_horizontalVelocity + Vector3.up * _verticalVelocity) * dt);
            if ((hits & CollisionFlags.Above) != 0 && _verticalVelocity > 0f) _verticalVelocity = 0f;

            if (wish.sqrMagnitude > 0.0001f)
            {
                Quaternion facing = Quaternion.LookRotation(wish, Vector3.up);
                transform.rotation = Quaternion.RotateTowards(transform.rotation, facing, turnSpeed * dt);
            }
        }

        void ApplyCameraTarget()
        {
            if (cameraTarget != null) cameraTarget.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        }

        void UpdateCursorLock()
        {
            var keyboard = Keyboard.current;
            var mouse = Mouse.current;

            if (Cursor.lockState == CursorLockMode.Locked)
            {
                if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame) SetCursorLocked(false);
            }
            else if (mouse != null && mouse.leftButton.wasPressedThisFrame)
            {
                // Clicking the game captures the mouse again, unless the click is on the session menu.
                Vector2 position = mouse.position.ReadValue();
                var guiPoint = new Vector2(position.x, Screen.height - position.y);
                if (!SessionMenu.ScreenRect.Contains(guiPoint)) SetCursorLocked(true);
            }
        }

        static void SetCursorLocked(bool locked)
        {
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }

        Vector3 SpawnPosition()
        {
            // Spread players around a small circle so they don't spawn inside each other.
            float angle = (OwnerClientId % 8) * 45f * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(angle) * 2.5f, 0.5f, Mathf.Cos(angle) * 2.5f - 6f);
        }

        void TeleportTo(Vector3 position, Quaternion rotation)
        {
            // A CharacterController ignores direct position changes unless it's briefly disabled.
            _controller.enabled = false;
            transform.SetPositionAndRotation(position, rotation);
            _controller.enabled = true;
            _horizontalVelocity = Vector3.zero;
            _verticalVelocity = 0f;
        }

        void ApplyColor()
        {
            if (bodyRenderer == null) return;
            var color = PlayerColors[(int)(OwnerClientId % (ulong)PlayerColors.Length)];
            bodyRenderer.material.color = color;
        }
    }
}
