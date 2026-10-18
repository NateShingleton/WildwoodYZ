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
    /// Lets the local player build. Controls:
    ///   Right click (hold and drag): draw a line or rectangle of blocks on the surface you started on.
    ///      While dragging, C switches to height: move the crosshair up/down to pull it into a 3D box.
    ///      Release to place. Escape (or left click) cancels.
    ///   Left click (hold and drag): the same, but removes.
    ///   1-8: material.  Scroll: brush size (1/2/4/8 blocks; drags snap to the brush grid).
    ///   C (when not dragging): chisel mode. Left click deletes the marked corner of the brush box.
    ///   Z: undo your last edit.
    ///   Left click a fallen piece: clear it away (until rubble turns into pickups).
    /// Runs before PlayerController so Escape can cancel a drag without also freeing the mouse.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public class BlockInteractor : NetworkBehaviour
    {
        [Tooltip("How far from the player's head you can start building, in meters.")]
        [SerializeField] float reach = 6f;

        [Tooltip("The head position reach is measured from (the player's camera target).")]
        [SerializeField] Transform eye;

        [Tooltip("How far away a drag can reach while drawing (meters).")]
        [SerializeField] float maxDragDistance = 40f;

        /// <summary>The frame in which Escape was used to cancel a drag (so it shouldn't also free the mouse).</summary>
        public static int EscapeConsumedFrame { get; private set; } = -1;

        static readonly int[] BrushSizes = { 1, 2, 4, 8 };

        static readonly Key[] MaterialKeys =
        {
            Key.Digit1, Key.Digit2, Key.Digit3, Key.Digit4, Key.Digit5, Key.Digit6, Key.Digit7, Key.Digit8,
        };

        enum DragKind
        {
            None,
            Place,
            Remove,
        }

        readonly RaycastHit[] _hits = new RaycastHit[16];
        readonly Collider[] _overlaps = new Collider[16];

        ushort _material = BlockMaterials.Stone;
        int _brushIndex;
        bool _chiselMode;
        bool _wasMouseCaptured;

        // What the crosshair is on.
        bool _hasTarget;
        int3 _targetBlock;   // the block under the crosshair
        int3 _placeBlock;    // the empty cell in front of it
        int _hitAxis;        // which axis the surface faces along (0 = x, 1 = y, 2 = z)
        int _targetCorner;   // nearest remaining corner of the chisel box
        FallingPiece _targetPiece; // a fallen piece under the crosshair, if any

        // The drag in progress. Positions are in brush cells (one cell = brush size in blocks).
        DragKind _drag;
        int _dragBrush;
        int3 _dragAnchor;
        int3 _dragEnd;
        int _dragAxis;
        bool _heightMode;
        int _dragHeight;

        string _message;
        float _messageUntil;

        LineRenderer _outline;
        Transform _cornerMarker;
        GUIStyle _crosshairStyle;

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
                _drag = DragKind.None;
                HideTarget();
                return;
            }

            var keyboard = Keyboard.current;
            var mouse = Mouse.current;
            if (mouse == null) return;

            if (_drag != DragKind.None)
            {
                UpdateDrag(view, keyboard, mouse);
                return;
            }

            HandleToolKeys(keyboard, mouse);
            if (keyboard != null && keyboard.zKey.wasPressedThisFrame) view.RequestUndo();

            Aim(view.World);
            ShowIdleTarget();
            if (!_hasTarget || !wasCaptured) return; // ignore the click that captured the mouse

            if (_chiselMode)
            {
                if (mouse.leftButton.wasPressedThisFrame)
                {
                    BrushBox(_targetBlock, BrushSize, out var min, out _);
                    view.RequestEdit(BlockEdit.Chisel(min, BrushSize, (byte)(1 << _targetCorner)));
                }
                return;
            }

            if (mouse.leftButton.wasPressedThisFrame && _targetPiece != null)
            {
                view.RequestRemovePiece(_targetPiece.Id);
                return;
            }

            if (mouse.rightButton.wasPressedThisFrame) BeginDrag(DragKind.Place, _placeBlock);
            else if (mouse.leftButton.wasPressedThisFrame) BeginDrag(DragKind.Remove, _targetBlock);
        }

        // ---------- Tools ----------

        void HandleToolKeys(Keyboard keyboard, Mouse mouse)
        {
            if (keyboard != null)
            {
                for (int i = 0; i < MaterialKeys.Length; i++)
                {
                    if (keyboard[MaterialKeys[i]].wasPressedThisFrame && BlockMaterials.IsValid((ushort)(i + 1)))
                        _material = (ushort)(i + 1);
                }
                if (keyboard.cKey.wasPressedThisFrame) _chiselMode = !_chiselMode;
            }

            float scroll = mouse.scroll.ReadValue().y;
            if (scroll > 0.01f) _brushIndex = Mathf.Min(_brushIndex + 1, BrushSizes.Length - 1);
            else if (scroll < -0.01f) _brushIndex = Mathf.Max(_brushIndex - 1, 0);
        }

        void Aim(BlockWorld world)
        {
            _hasTarget = false;
            _targetPiece = null;
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

            _targetPiece = hit.collider.GetComponentInParent<FallingPiece>();

            float size = BlockConstants.BlockSize;
            _targetBlock = ToBlock(hit.point - hit.normal * (size * 0.25f));
            _placeBlock = ToBlock(hit.point + hit.normal * (size * 0.25f));
            _hitAxis = DominantAxis(hit.normal);

            // For chiseling, treat the brush-sized group as one big block and pick its nearest remaining corner.
            int n = BrushSize;
            BrushBox(_targetBlock, n, out var regionMin, out _);
            byte regionCorners = BlockEditor.RegionCorners(world, regionMin, n);
            Vector3 regionLocal = (hit.point / size - ToVector(regionMin)) / n;
            _targetCorner = NearestCorner(regionCorners, regionLocal);
            _hasTarget = true;
        }

        // ---------- Dragging ----------

        void BeginDrag(DragKind kind, int3 block)
        {
            _drag = kind;
            _dragBrush = BrushSize;
            _dragAnchor = CellOf(block, _dragBrush);
            _dragEnd = _dragAnchor;
            _dragAxis = _hitAxis;
            _heightMode = false;
            _dragHeight = 0;
            DragBox(out var min, out var max);
            DrawOutline(min, max, 0.016f);
            _cornerMarker.gameObject.SetActive(false);
        }

        void UpdateDrag(BlockWorldView view, Keyboard keyboard, Mouse mouse)
        {
            bool escape = keyboard != null && keyboard.escapeKey.wasPressedThisFrame;
            bool otherButton = _drag == DragKind.Place ? mouse.leftButton.wasPressedThisFrame : mouse.rightButton.wasPressedThisFrame;
            if (escape || otherButton)
            {
                if (escape) EscapeConsumedFrame = Time.frameCount;
                CancelDrag("Cancelled");
                return;
            }

            if (keyboard != null && keyboard.cKey.wasPressedThisFrame) _heightMode = !_heightMode;

            var camera = Camera.main;
            if (camera != null)
            {
                Ray ray = camera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
                if (_heightMode) UpdateHeight(ray);
                else UpdateFootprint(ray);
            }

            DragBox(out var min, out var max);
            DrawOutline(min, max, 0.016f);

            var button = _drag == DragKind.Place ? mouse.rightButton : mouse.leftButton;
            if (!button.isPressed) Commit(view, min, max);
        }

        /// <summary>Where the crosshair meets the surface plane the drag started on.</summary>
        void UpdateFootprint(Ray ray)
        {
            float cellWorld = _dragBrush * BlockConstants.BlockSize;
            float planeCoordinate = (_dragAnchor[_dragAxis] + 0.5f) * cellWorld;
            float directionAlong = Component(ray.direction, _dragAxis);
            if (Mathf.Abs(directionAlong) < 1e-4f) return; // looking exactly along the plane

            float t = (planeCoordinate - Component(ray.origin, _dragAxis)) / directionAlong;
            if (t <= 0f || t > maxDragDistance) return;

            Vector3 point = ray.GetPoint(t);
            var cell = new int3(
                Mathf.FloorToInt(point.x / cellWorld),
                Mathf.FloorToInt(point.y / cellWorld),
                Mathf.FloorToInt(point.z / cellWorld));
            cell[_dragAxis] = _dragAnchor[_dragAxis];

            int limit = MaxCellsPerSide() - 1;
            _dragEnd = math.clamp(cell, _dragAnchor - limit, _dragAnchor + limit);
            _dragEnd[_dragAxis] = _dragAnchor[_dragAxis];
        }

        /// <summary>Pull the footprint out of the surface: the box grows to where the crosshair passes the dragged corner.</summary>
        void UpdateHeight(Ray ray)
        {
            float cellWorld = _dragBrush * BlockConstants.BlockSize;
            Vector3 axis = AxisVector(_dragAxis);
            Vector3 lineStart = (ToVector(_dragEnd) + Vector3.one * 0.5f) * cellWorld;

            // Closest point between the camera ray and the line through the dragged corner.
            Vector3 w0 = ray.origin - lineStart;
            float a = Vector3.Dot(ray.direction, ray.direction);
            float b = Vector3.Dot(ray.direction, axis);
            float c = Vector3.Dot(axis, axis);
            float d = Vector3.Dot(ray.direction, w0);
            float e = Vector3.Dot(axis, w0);
            float denominator = a * c - b * b;
            if (Mathf.Abs(denominator) < 1e-4f) return; // looking straight along the line

            float along = (a * e - b * d) / denominator;
            int limit = MaxCellsPerSide() - 1;
            _dragHeight = Mathf.Clamp(Mathf.RoundToInt(along / cellWorld), -limit, limit);
        }

        void Commit(BlockWorldView view, int3 min, int3 max)
        {
            DragKind kind = _drag;
            _drag = DragKind.None;
            _heightMode = false;

            int3 size = max - min + 1;
            long volume = (long)size.x * size.y * size.z;
            if (math.any(size > BlockEditor.MaxEditSize) || volume > BlockEditor.MaxEditVolume)
            {
                ShowMessage($"Too big ({size.x} x {size.y} x {size.z}). Max {BlockEditor.MaxEditVolume:N0} blocks at once.");
                return;
            }

            if (kind == DragKind.Place)
            {
                if (OverlapsAnyPlayer(min, max))
                {
                    ShowMessage("Can't build inside a player.");
                    return;
                }
                view.RequestEdit(BlockEdit.FillBox(min, max, Block.Solid(_material), FillMode.OnlyAir));
            }
            else
            {
                view.RequestEdit(BlockEdit.RemoveBox(min, max));
            }
        }

        void CancelDrag(string message)
        {
            _drag = DragKind.None;
            _heightMode = false;
            ShowMessage(message);
        }

        /// <summary>The drag's box in block coordinates.</summary>
        void DragBox(out int3 min, out int3 max)
        {
            int3 minCell = math.min(_dragAnchor, _dragEnd);
            int3 maxCell = math.max(_dragAnchor, _dragEnd);
            int layer = _dragAnchor[_dragAxis];
            minCell[_dragAxis] = Mathf.Min(layer, layer + _dragHeight);
            maxCell[_dragAxis] = Mathf.Max(layer, layer + _dragHeight);
            min = minCell * _dragBrush;
            max = (maxCell + 1) * _dragBrush - 1;
        }

        int MaxCellsPerSide() => Mathf.Max(1, BlockEditor.MaxEditSize / _dragBrush);

        // ---------- Helpers ----------

        /// <summary>The corner in <paramref name="corners"/> closest to a point in unit-cube coordinates (0 to 1).</summary>
        static int NearestCorner(byte corners, Vector3 local)
        {
            int best = 0;
            float bestDistance = float.MaxValue;
            for (int corner = 0; corner < 8; corner++)
            {
                if (corners != 0 && (corners & (1 << corner)) == 0) continue;
                float distance = (CornerShapes.CornerPosition(corner) - local).sqrMagnitude;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = corner;
                }
            }
            return best;
        }

        /// <summary>The brush cell containing a block. Big brushes snap to their own grid, so 40 cm bricks line up.</summary>
        static int3 CellOf(int3 block, int brush) =>
            new int3(FloorDiv(block.x, brush), FloorDiv(block.y, brush), FloorDiv(block.z, brush));

        static void BrushBox(int3 block, int brush, out int3 min, out int3 max)
        {
            min = CellOf(block, brush) * brush;
            max = min + (brush - 1);
        }

        static int FloorDiv(int value, int divisor) => (int)math.floor(value / (float)divisor);

        static int DominantAxis(Vector3 normal)
        {
            Vector3 n = new Vector3(Mathf.Abs(normal.x), Mathf.Abs(normal.y), Mathf.Abs(normal.z));
            if (n.y >= n.x && n.y >= n.z) return 1;
            return n.x >= n.z ? 0 : 2;
        }

        static float Component(Vector3 v, int axis) => axis == 0 ? v.x : axis == 1 ? v.y : v.z;

        static Vector3 AxisVector(int axis) => axis == 0 ? Vector3.right : axis == 1 ? Vector3.up : Vector3.forward;

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

        void ShowMessage(string message)
        {
            _message = message;
            _messageUntil = Time.unscaledTime + 2.5f;
        }

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
            var markerRenderer = marker.GetComponent<MeshRenderer>();
            if (material != null) markerRenderer.sharedMaterial = material;
            markerRenderer.shadowCastingMode = ShadowCastingMode.Off;
            _cornerMarker = marker.transform;
            marker.SetActive(false);
        }

        void HideTarget()
        {
            if (_outline == null) return;
            _outline.gameObject.SetActive(false);
            _cornerMarker.gameObject.SetActive(false);
        }

        /// <summary>Outline when not dragging: where a click would place (or, in chisel mode, the box being chiseled).</summary>
        void ShowIdleTarget()
        {
            if (_outline == null) return;
            if (!_hasTarget)
            {
                HideTarget();
                return;
            }

            int n = BrushSize;
            BrushBox(_chiselMode ? _targetBlock : _placeBlock, n, out var min, out var max);
            DrawOutline(min, max, 0.01f);

            _cornerMarker.gameObject.SetActive(_chiselMode);
            if (_chiselMode)
            {
                Vector3 corner = ToVector(min) + CornerShapes.CornerPosition(_targetCorner) * n;
                _cornerMarker.position = corner * BlockConstants.BlockSize;
                _cornerMarker.localScale = Vector3.one * (0.025f + 0.006f * n);
            }
        }

        void DrawOutline(int3 min, int3 max, float width)
        {
            if (_outline == null) return;
            _outline.gameObject.SetActive(true);
            _outline.widthMultiplier = width;

            float size = BlockConstants.BlockSize;
            Vector3 low = ToVector(min) * size - Vector3.one * 0.003f; // a hair larger so lines don't flicker into blocks
            Vector3 high = ToVector(max + 1) * size + Vector3.one * 0.003f;
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

        void OnGUI()
        {
            if (!IsSpawned || !IsOwner || Cursor.lockState != CursorLockMode.Locked) return;

            if (_crosshairStyle == null)
            {
                _crosshairStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 20 };
            }
            GUI.Label(new Rect(Screen.width * 0.5f - 15f, Screen.height * 0.5f - 15f, 30f, 30f), "+", _crosshairStyle);

            string material = BlockMaterials.Get(_material).Name;
            string line1, line2;
            if (_drag != DragKind.None)
            {
                DragBox(out var min, out var max);
                int3 size = max - min + 1;
                string verb = _drag == DragKind.Place ? "Placing" : "Removing";
                line1 = $"{verb} {size.x} x {size.y} x {size.z} blocks  ({size.x * 0.1f:0.0} x {size.y * 0.1f:0.0} x {size.z * 0.1f:0.0} m)";
                line2 = _heightMode
                    ? "HEIGHT: move the crosshair to pull it out  -  C: back to drawing  -  release to finish  -  Esc cancels"
                    : "Drag to draw a line or rectangle  -  C: pull up into a box  -  release to finish  -  Esc cancels";
            }
            else if (_chiselMode)
            {
                line1 = $"CHISEL  -  left click deletes the marked corner of the box  -  brush {BrushSize} (scroll)  -  C to go back";
                line2 = "Z: undo    V: first / third person";
            }
            else
            {
                line1 = $"BUILD  -  {material} (1-8)  -  brush {BrushSize} block{(BrushSize > 1 ? "s" : "")} = {BrushSize * 10} cm (scroll)";
                line2 = _targetPiece != null
                    ? "Left click: clear this fallen piece    Right click: place    Z: undo    V: view"
                    : "Hold right click: draw & place    Hold left click: draw & remove    C: chisel    Z: undo    V: view";
            }

            var area = new Rect(16f, Screen.height - 104f, 760f, 88f);
            GUILayout.BeginArea(area, GUI.skin.box);
            GUILayout.Label(line1);
            GUILayout.Label(line2);
            if (_message != null && Time.unscaledTime < _messageUntil) GUILayout.Label(_message);
            GUILayout.EndArea();
        }
    }
}
