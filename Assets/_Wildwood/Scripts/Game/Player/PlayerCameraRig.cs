using Unity.Cinemachine;
using UnityEngine;

namespace Wildwood.Game.Player
{
    /// <summary>
    /// Sits on the scene's player camera. Only the local player's character hands it a target, so each window
    /// follows its own player. Switching views glides the camera between the over-the-shoulder spot and the
    /// player's eyes by shrinking the third-person offsets to zero.
    /// </summary>
    [RequireComponent(typeof(CinemachineCamera))]
    public class PlayerCameraRig : MonoBehaviour
    {
        public static PlayerCameraRig Instance { get; private set; }

        [SerializeField] CinemachineCamera followCamera;
        [SerializeField] CinemachineThirdPersonFollow thirdPerson;

        [Tooltip("Seconds to glide between first and third person.")]
        [SerializeField] float switchSeconds = 0.2f;

        Vector3 _shoulderOffset;
        float _verticalArmLength;
        float _cameraDistance;
        Vector3 _damping;
        bool _firstPerson;
        float _blend; // 0 = third person, 1 = first person

        public bool IsFirstPerson => _firstPerson;

        void Awake()
        {
            Instance = this;
            if (followCamera == null) followCamera = GetComponent<CinemachineCamera>();
            if (thirdPerson == null) thirdPerson = GetComponent<CinemachineThirdPersonFollow>();
            if (thirdPerson != null)
            {
                _shoulderOffset = thirdPerson.ShoulderOffset;
                _verticalArmLength = thirdPerson.VerticalArmLength;
                _cameraDistance = thirdPerson.CameraDistance;
                _damping = thirdPerson.Damping;
            }
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        public void Follow(Transform target)
        {
            if (followCamera != null) followCamera.Follow = target;
        }

        public void Release(Transform target)
        {
            if (followCamera != null && followCamera.Follow == target) followCamera.Follow = null;
        }

        public void SetFirstPerson(bool firstPerson)
        {
            _firstPerson = firstPerson;
        }

        // Update (not LateUpdate) so the new offsets are in place before Cinemachine positions the camera.
        void Update()
        {
            if (thirdPerson == null) return;

            float target = _firstPerson ? 1f : 0f;
            float step = switchSeconds > 0f ? Time.unscaledDeltaTime / switchSeconds : 1f;
            _blend = Mathf.MoveTowards(_blend, target, step);
            float k = Mathf.SmoothStep(0f, 1f, _blend);

            thirdPerson.ShoulderOffset = Vector3.Lerp(_shoulderOffset, Vector3.zero, k);
            thirdPerson.VerticalArmLength = Mathf.Lerp(_verticalArmLength, 0f, k);
            thirdPerson.CameraDistance = Mathf.Lerp(_cameraDistance, 0f, k);
            thirdPerson.Damping = Vector3.Lerp(_damping, Vector3.zero, k); // no lag when it's your own eyes
        }
    }
}
