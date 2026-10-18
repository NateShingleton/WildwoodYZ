using Unity.Mathematics;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using Wildwood.Core.Blocks;
using Wildwood.Game.Player;

namespace Wildwood.Game.Blocks
{
    /// <summary>
    /// Lets the local player aim at blocks and place, remove or chisel them.
    /// Temporary controls until the full building tools (step 4):
    ///   Left click: remove     Right click: place     1-8: material     Scroll: brush size
    ///   C: chisel mode (left click deletes the corner nearest the crosshair)
    /// </summary>
    public class BlockInteractor : NetworkBehaviour
    {
        [Tooltip("How far from the player's head you can build, in meters.")]
        [SerializeField] float reach = 6f;

        [Tooltip("The head position reach is measured from (the player's camera target).")]
        [SerializeField] Transform eye;

        static readonly int[] BrushSizes = { 1, 2, 4, 8 };

        static readonly Key[] MaterialKeys =
        {
            Key.Digit1, Key.Digit2, Key.Digit3, Key.Digit4, Key.Digit5, Key.Digit6, Key.Digit7, Key.Digit8,
        };

        readonly RaycastHit[] _hits = new RaycastHit[16];
        readonly Collider[] _overlaps = new Collider[16];

        ushort _material = BlockMaterials.Stone;
        int _brushIndex;
        bool _chiselMode;
        bool _wasMouseCaptured;

        bool _hasTarget;
        int3 _targetBlock;   // the block under the crosshair
        int3 _placeBlock;    // the empty cell in front of it
        int _targetCorner;   // nearest existing corner of the target block (chisel)

        LineRenderer _outline;
        Transform _cornerMarker;

        int BrushSize => BrushSizes[_brushIndex];

        public override void OnNetworkSpawn()
        {
            if (!IsOwner)
            {
                enabled = false; // only the local player builds from this machine
                return;
            }
            CreateTargetVisuals();
        }

        public override void OnNetworkDespawn()
        {
            if (_outline != null) Destroy(_outline.gameObject);
            if (_cornerMarker != null) Destroy(_cornerMarker.gameObject);
        }

        void Update()
        {
            if (!IsSpawned || !IsOwner) return;

            bool captured = Cursor.lockState == CursorLockMode.Locked;
            bool wasCaptured = _wasMouseCaptured;
            _wasMouseCaptured = captured;

            var view = BlockWorldView.Instance;
            if (!captured || view == null || !view.CanEdit)
            {
                SetTargetVisible(false);
                return;
            }

            HandleToolKeys();
            Aim(view.World);
            SetTargetVisible(_hasTarget);
            if (!_hasTarget || !wasCaptured) return; // ignore the click that captured the mouse

            var mouse = Mouse.current;
            if (mouse == null) return;

            if (mouse.leftButton.wasPressedThisFrame)
            {
                if (_chiselMode) view.RequestEdit(BlockEdit.Chisel(_targetBlock, (byte)(1 << _targetCorner)));
                else
                {
                    BrushBox(_targetBlock, out var min, out var max);
                    view.RequestEdit(BlockEdit.RemoveBox(min, max));
                }
            }
            else if (mouse.rightButton.wasPressedThisFrame && !_chiselMode)
            {
                BrushBox(_placeBlock, out var min, out var max);
                if (!OverlapsAnyPlayer(min, max))
                    view.RequestEdit(BlockEdit.FillBox(min, max, Block.Solid(_material), FillMode.OnlyAir));
            }
        }

        void HandleToolKeys()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null)
            {
                for (int i = 0; i < MaterialKeys.Length; i++)
                {
                    if (keyboard[MaterialKeys[i]].wasPressedThisFrame && BlockMaterials.IsValid((ushort)(i + 1)))
                        _material = (ushort)(i + 1);
                }
                if (keyboard.cKey.wasPressedThisFrame) _chiselMode = !_chiselMode;
            }

            var mouse = Mouse.current;
            if (mouse != null)
            {
                float scroll = mouse.scroll.ReadValue().y;
                if (scroll > 0.01f) _brushIndex = Mathf.Min(_brushIndex + 1, BrushSizes.Length - 1);
                else if (scroll < -0.01f) _brushIndex = Mathf.Max(_brushIndex - 1, 0);
            }
        }

        void Aim(BlockWorld world)
        {
            _hasTarget = false;
            var camera = Camera.main;
            if (camera == null) return;

            Ray ray = camera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            float maxDistance = reach + 10f; // the camera sits behind the player, so allow for that
            int count = Physics.RaycastNonAlloc(ray, _hits, maxDistance, ~0, QueryTriggerInteraction.Ignore);

            int best = -1;
            for (int i = 0; i < count; i++)
            {
                if (_hits[i].collider.GetComponentInParent<PlayerController>() != null) continue; // ignore players
                if (best < 0 || _hits[i].distance < _hits[best].distance) best = i;
            }
            if (best < 0) return;

            RaycastHit hit = _hits[best];
            Vector3 head = eye != null ? eye.position : transform.position + Vector3.up * 1.6f;
            if ((hit.point - head).sqrMagnitude > reach * reach) return;

            float size = BlockConstants.BlockSize;
            _targetBlock = ToBlock(hit.point - hit.normal * (size * 0.25f));
            _placeBlock = ToBlock(hit.point + hit.normal * (size * 0.25f));
            _targetCorner = NearestCorner(world.GetBlock(_targetBlock), hit.point / size - ToVector(_targetBlock));
            _hasTarget = true;
        }

        /// <summary>The existing corner of <paramref name="block"/> closest to a point given in block-local units (0 to 1).</summary>
        static int NearestCorner(Block block, Vector3 local)
        {
            int best = 0;
            float bestDistance = float.MaxValue;
            for (int corner = 0; corner < 8; corner++)
            {
                if (!block.IsAir && (block.Corners & (1 << corner)) == 0) continue;
                float distance = (CornerShapes.CornerPosition(corner) - local).sqrMagnitude;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = corner;
                }
            }
            return best;
        }

        /// <summary>The brush-sized box containing a cell. Big brushes snap to their own grid, so 40 cm bricks line up.</summary>
        void BrushBox(int3 cell, out int3 min, out int3 max)
        {
            int n = BrushSize;
            min = new int3(FloorToMultiple(cell.x, n), FloorToMultiple(cell.y, n), FloorToMultiple(cell.z, n));
            max = min + (n - 1);
        }

        static int FloorToMultiple(int value, int multiple) => (int)math.floor(value / (float)multiple) * multiple;

        bool OverlapsAnyPlayer(int3 min, int3 max)
        {
            float size = BlockConstants.BlockSize;
            Vector3 low = ToVector(min) * size;
            Vector3 high = ToVector(max + 1) * size;
            Vector3 halfExtents = (high - low) * 0.5f - Vector3.one * 0.005f;
            int count = Physics.OverlapBoxNonAlloc((low + high) * 0.5f, halfExtents, _overlaps, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                if (_overlaps[i].GetComponentInParent<PlayerController>() != null) return true;
            }
            return false;
        }

        static int3 ToBlock(Vector3 worldPosition)
        {
            float size = BlockConstants.BlockSize;
            return new int3(
                Mathf.FloorToInt(worldPosition.x / size),
                Mathf.FloorToInt(worldPosition.y / size),
                Mathf.FloorToInt(worldPosition.z / size));
        }

        static Vector3 ToVector(int3 v) => new Vector3(v.x, v.y, v.z);

        // ---------- Target outline ----------

        void CreateTargetVisuals()
        {
            var material = BlockWorldView.Instance != null ? BlockWorldView.Instance.OutlineMaterial : null;

            var outlineObject = new GameObject("Block Target Outline");
            _outline = outlineObject.AddComponent<LineRenderer>();
            _outline.sharedMaterial = material;
            _outline.useWorldSpace = true;
            _outline.loop = false;
            _outline.positionCount = 16;
            _outline.widthMultiplier = 0.01f;
            _outline.numCapVertices = 0;
            _outline.shadowCastingMode = ShadowCastingMode.Off;
            _outline.receiveShadows = false;
            outlineObject.SetActive(false);

            var marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
            marker.name = "Chisel Corner Marker";
            Destroy(marker.GetComponent<Collider>());
            marker.transform.localScale = Vector3.one * 0.025f;
            var markerRenderer = marker.GetComponent<MeshRenderer>();
            if (material != null) markerRenderer.sharedMaterial = material;
            markerRenderer.shadowCastingMode = ShadowCastingMode.Off;
            _cornerMarker = marker.transform;
            marker.SetActive(false);
        }

        void SetTargetVisible(bool visible)
        {
            if (_outline == null) return;
            _outline.gameObject.SetActive(visible);
            _cornerMarker.gameObject.SetActive(visible && _chiselMode);
            if (!visible) return;

            float size = BlockConstants.BlockSize;
            int3 min = _targetBlock, max = _targetBlock;
            if (!_chiselMode) BrushBox(_targetBlock, out min, out max);
            DrawBox(ToVector(min) * size, ToVector(max + 1) * size);

            if (_chiselMode)
                _cornerMarker.position = (ToVector(_targetBlock) + CornerShapes.CornerPosition(_targetCorner)) * size;
        }

        void DrawBox(Vector3 low, Vector3 high)
        {
            low -= Vector3.one * 0.003f; // a hair larger than the block so the lines don't flicker into it
            high += Vector3.one * 0.003f;
            var p0 = new Vector3(low.x, low.y, low.z);
            var p1 = new Vector3(high.x, low.y, low.z);
            var p2 = new Vector3(high.x, low.y, high.z);
            var p3 = new Vector3(low.x, low.y, high.z);
            var p4 = new Vector3(low.x, high.y, low.z);
            var p5 = new Vector3(high.x, high.y, low.z);
            var p6 = new Vector3(high.x, high.y, high.z);
            var p7 = new Vector3(low.x, high.y, high.z);
            // One continuous line that runs along all 12 edges.
            _outline.SetPosition(0, p0);
            _outline.SetPosition(1, p1);
            _outline.SetPosition(2, p2);
            _outline.SetPosition(3, p3);
            _outline.SetPosition(4, p0);
            _outline.SetPosition(5, p4);
            _outline.SetPosition(6, p5);
            _outline.SetPosition(7, p1);
            _outline.SetPosition(8, p5);
            _outline.SetPosition(9, p6);
            _outline.SetPosition(10, p2);
            _outline.SetPosition(11, p6);
            _outline.SetPosition(12, p7);
            _outline.SetPosition(13, p3);
            _outline.SetPosition(14, p7);
            _outline.SetPosition(15, p4);
        }

        // ---------- HUD ----------

        GUIStyle _crosshairStyle;

        void OnGUI()
        {
            if (!IsSpawned || !IsOwner || Cursor.lockState != CursorLockMode.Locked) return;

            if (_crosshairStyle == null)
            {
                _crosshairStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 20 };
            }
            GUI.Label(new Rect(Screen.width * 0.5f - 15f, Screen.height * 0.5f - 15f, 30f, 30f), "+", _crosshairStyle);

            string material = BlockMaterials.Get(_material).Name;
            string brush = $"{BrushSize} block{(BrushSize > 1 ? "s" : "")} ({BrushSize * 10} cm)";
            string mode = _chiselMode
                ? "CHISEL  -  left click deletes the marked corner  -  C to go back"
                : "BUILD  -  left click remove, right click place  -  C for chisel";

            var area = new Rect(16f, Screen.height - 96f, 520f, 80f);
            GUILayout.BeginArea(area, GUI.skin.box);
            GUILayout.Label(mode);
            GUILayout.Label($"Material: {material} (keys 1-8)     Brush: {brush} (scroll)");
            GUILayout.EndArea();
        }
    }
}
