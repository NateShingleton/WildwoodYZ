using Unity.Cinemachine;
using UnityEngine;

namespace Wildwood.Game.Player
{
    /// <summary>
    /// Sits on the scene's third-person follow camera. Only the local player's character
    /// hands it a target, so each window follows its own player.
    /// </summary>
    [RequireComponent(typeof(CinemachineCamera))]
    public class PlayerCameraRig : MonoBehaviour
    {
        public static PlayerCameraRig Instance { get; private set; }

        [SerializeField] CinemachineCamera followCamera;

        void Awake()
        {
            Instance = this;
            if (followCamera == null) followCamera = GetComponent<CinemachineCamera>();
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
    }
}
