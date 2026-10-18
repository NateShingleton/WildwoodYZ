using System.Collections.Generic;
using UnityEngine;

namespace Wildwood.Game.Blocks
{
    /// <summary>One loose piece of blocks. Created and owned by <see cref="FallingPieces"/>.</summary>
    public class FallingPiece : MonoBehaviour
    {
        public readonly List<Mesh> Meshes = new List<Mesh>();

        public uint Id { get; private set; }

        Rigidbody _body;
        bool _simulated;
        bool _wasMoving;
        bool _hasTarget;
        Vector3 _targetPosition;
        Quaternion _targetRotation;

        public void Initialize(uint id, Rigidbody body, bool simulated)
        {
            Id = id;
            _body = body;
            _simulated = simulated;
            _wasMoving = true;
        }

        /// <summary>Host: true while the piece moves, plus once more right after it comes to rest.</summary>
        public bool NeedsSync()
        {
            bool moving = _body != null && !_body.IsSleeping();
            bool send = moving || _wasMoving;
            _wasMoving = moving;
            return send;
        }

        /// <summary>Clients: where the host says the piece is now.</summary>
        public void SetTarget(Vector3 position, Quaternion rotation)
        {
            _targetPosition = position;
            _targetRotation = rotation;
            _hasTarget = true;
        }

        void FixedUpdate()
        {
            if (_simulated || !_hasTarget || _body == null) return;
            // Glide toward the latest position from the host. Positions arrive ~20 times a second.
            float blend = 1f - Mathf.Exp(-Time.fixedDeltaTime * 14f);
            _body.MovePosition(Vector3.Lerp(_body.position, _targetPosition, blend));
            _body.MoveRotation(Quaternion.Slerp(_body.rotation, _targetRotation, blend));
        }

        void OnDestroy()
        {
            foreach (var mesh in Meshes)
            {
                if (mesh != null) Destroy(mesh);
            }
        }
    }
}
