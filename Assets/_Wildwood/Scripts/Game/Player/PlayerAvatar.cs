using System.Collections.Generic;
using UnityEngine;
using Wildwood.Core.Meshing;

namespace Wildwood.Game.Player
{
    /// <summary>
    /// An original low-poly Norse-style character built from tapered boxes and wrapped in tiny pixel textures:
    /// braided beard, wool tunic with a woven band, leather belt, bracers and boots. Each player gets their own
    /// tunic, hair and skin colors. It animates itself from how fast it's moving (walk, run, jump, idle),
    /// so it works the same for your own character and for other players' characters, with no extra network data.
    /// Built in code, so there's no model file to import. Faces +Z, feet at y = 0, about 1.8 m tall.
    /// </summary>
    public class PlayerAvatar : MonoBehaviour
    {
        [Tooltip("URP Lit material the character's materials are copied from.")]
        [SerializeField] Material baseMaterial;

        [Tooltip("Meters per 32-pixel texture repeat. Bigger = chunkier pixels.")]
        [SerializeField] float metersPerTexture = 0.5f;

        static readonly Color[] TunicColors =
        {
            new Color(0.55f, 0.40f, 0.24f), new Color(0.31f, 0.42f, 0.25f), new Color(0.29f, 0.37f, 0.52f),
            new Color(0.56f, 0.25f, 0.18f), new Color(0.55f, 0.53f, 0.48f), new Color(0.66f, 0.52f, 0.21f),
        };

        static readonly Color[] HairColors =
        {
            new Color(0.72f, 0.33f, 0.12f), new Color(0.26f, 0.17f, 0.10f), new Color(0.82f, 0.68f, 0.40f),
            new Color(0.11f, 0.10f, 0.09f), new Color(0.50f, 0.21f, 0.10f), new Color(0.58f, 0.53f, 0.48f),
        };

        static readonly Color[] SkinColors =
        {
            new Color(0.88f, 0.66f, 0.52f), new Color(0.79f, 0.57f, 0.43f), new Color(0.63f, 0.44f, 0.31f), new Color(0.93f, 0.74f, 0.60f),
        };

        static readonly Color TrouserColor = new Color(0.33f, 0.28f, 0.23f);
        static readonly Color LeatherColor = new Color(0.33f, 0.22f, 0.14f);
        static readonly Color MetalColor = new Color(0.66f, 0.63f, 0.57f);
        static readonly Color EyeColor = new Color(0.78f, 0.86f, 0.92f);

        readonly Dictionary<PixelTextures.Kind, List<Renderer>> _renderersByKind = new Dictionary<PixelTextures.Kind, List<Renderer>>();
        readonly List<Mesh> _meshes = new List<Mesh>();
        readonly List<Material> _materials = new List<Material>();
        readonly MeshBuffers _buffers = new MeshBuffers();

        Transform _root, _hips, _torso, _head;
        Transform _shoulderL, _shoulderR, _elbowL, _elbowR, _hipL, _hipR, _kneeL, _kneeR;
        Vector3 _hipsRest;
        Vector3 _lastPosition;
        float _speed, _verticalSpeed, _phase, _air;
        bool _built;

        void Awake()
        {
            Build();
            ApplyLook(0);
            _lastPosition = transform.position;
        }

        void OnDestroy()
        {
            foreach (var mesh in _meshes) if (mesh != null) Destroy(mesh);
            foreach (var material in _materials) if (material != null) Destroy(material);
        }

        /// <summary>Picks this player's colors (stable for a given player number).</summary>
        public void ApplyLook(ulong player)
        {
            if (!_built) return;
            int i = (int)(player % 997);
            var colors = new Dictionary<PixelTextures.Kind, Color>
            {
                [PixelTextures.Kind.Cloth] = TunicColors[i % TunicColors.Length],
                [PixelTextures.Kind.Hair] = HairColors[(i * 7 + 3) % HairColors.Length],
                [PixelTextures.Kind.Skin] = SkinColors[(i * 5 + 1) % SkinColors.Length],
                [PixelTextures.Kind.Trousers] = TrouserColor,
                [PixelTextures.Kind.Leather] = LeatherColor,
                [PixelTextures.Kind.Metal] = MetalColor,
                [PixelTextures.Kind.Eyes] = EyeColor,
            };

            foreach (var material in _materials) if (material != null) Destroy(material);
            _materials.Clear();

            foreach (var pair in _renderersByKind)
            {
                Material material = baseMaterial != null ? new Material(baseMaterial) : new Material(Shader.Find("Universal Render Pipeline/Lit"));
                material.name = $"Avatar {pair.Key}";
                material.SetTexture("_BaseMap", PixelTextures.Get(pair.Key, colors[pair.Key]));
                material.SetColor("_BaseColor", Color.white);
                material.SetFloat("_Smoothness", pair.Key == PixelTextures.Kind.Metal ? 0.45f : 0.05f);
                _materials.Add(material);
                foreach (var part in pair.Value) part.sharedMaterial = material;
            }
        }

        // ---------- Building the body ----------

        void Build()
        {
            if (_built) return;
            _built = true;

            const PixelTextures.Kind Cloth = PixelTextures.Kind.Cloth;
            const PixelTextures.Kind Leather = PixelTextures.Kind.Leather;
            const PixelTextures.Kind Skin = PixelTextures.Kind.Skin;
            const PixelTextures.Kind Hair = PixelTextures.Kind.Hair;
            const PixelTextures.Kind Eyes = PixelTextures.Kind.Eyes;
            const PixelTextures.Kind Metal = PixelTextures.Kind.Metal;

            // Slightly lowered: the character controller floats a couple of centimeters above the ground.
            _root = Joint("Avatar", transform, new Vector3(0f, -0.02f, 0f));
            _hips = Joint("Hips", _root, new Vector3(0f, 0.94f, 0f));
            _hipsRest = _hips.localPosition;

            // Tunic skirt, belt and buckle.
            Part("Tunic Skirt", _hips, Cloth, new Vector3(0f, -0.07f, 0f), 0.18f, new Vector2(0.41f, 0.27f), new Vector2(0.38f, 0.25f));
            Part("Belt", _hips, Leather, new Vector3(0f, 0.02f, 0f), 0.06f, new Vector2(0.395f, 0.26f), new Vector2(0.39f, 0.255f));
            Part("Buckle", _hips, Metal, new Vector3(0f, 0.02f, 0.13f), 0.05f, new Vector2(0.06f, 0.02f), new Vector2(0.06f, 0.02f));

            _torso = Joint("Torso", _hips, new Vector3(0f, 0.02f, 0f));
            Part("Chest", _torso, Cloth, new Vector3(0f, 0.27f, 0f), 0.50f, new Vector2(0.38f, 0.24f), new Vector2(0.50f, 0.27f));
            Part("Collar", _torso, Cloth, new Vector3(0f, 0.53f, 0f), 0.05f, new Vector2(0.30f, 0.20f), new Vector2(0.22f, 0.17f));
            Part("Neck", _torso, Skin, new Vector3(0f, 0.57f, 0f), 0.08f, new Vector2(0.12f, 0.12f), new Vector2(0.11f, 0.11f));

            _head = Joint("Head", _torso, new Vector3(0f, 0.59f, 0.01f));
            Part("Head", _head, Skin, new Vector3(0f, 0.13f, 0f), 0.26f, new Vector2(0.19f, 0.21f), new Vector2(0.20f, 0.22f));
            Part("Hair Top", _head, Hair, new Vector3(0f, 0.235f, -0.01f), 0.075f, new Vector2(0.215f, 0.235f), new Vector2(0.19f, 0.21f));
            Part("Hair Back", _head, Hair, new Vector3(0f, 0.13f, -0.098f), 0.22f, new Vector2(0.21f, 0.05f), new Vector2(0.215f, 0.05f));
            Part("Beard", _head, Hair, new Vector3(0f, 0.02f, 0.085f), 0.17f, new Vector2(0.12f, 0.08f), new Vector2(0.19f, 0.07f));
            Part("Moustache", _head, Hair, new Vector3(0f, 0.088f, 0.113f), 0.025f, new Vector2(0.11f, 0.02f), new Vector2(0.10f, 0.02f));
            Part("Braid", _head, Hair, new Vector3(0f, -0.1f, 0.1f), 0.09f, new Vector2(0.035f, 0.035f), new Vector2(0.045f, 0.045f));
            Part("Braid Bead", _head, Metal, new Vector3(0f, -0.155f, 0.1f), 0.03f, new Vector2(0.04f, 0.04f), new Vector2(0.04f, 0.04f));
            Part("Nose", _head, Skin, new Vector3(0f, 0.12f, 0.113f), 0.06f, new Vector2(0.04f, 0.035f), new Vector2(0.025f, 0.02f));
            Part("Eye L", _head, Eyes, new Vector3(-0.045f, 0.148f, 0.107f), 0.018f, new Vector2(0.035f, 0.01f), new Vector2(0.035f, 0.01f));
            Part("Eye R", _head, Eyes, new Vector3(0.045f, 0.148f, 0.107f), 0.018f, new Vector2(0.035f, 0.01f), new Vector2(0.035f, 0.01f));
            Part("Brow L", _head, Hair, new Vector3(-0.045f, 0.172f, 0.108f), 0.016f, new Vector2(0.05f, 0.016f), new Vector2(0.05f, 0.016f));
            Part("Brow R", _head, Hair, new Vector3(0.045f, 0.172f, 0.108f), 0.016f, new Vector2(0.05f, 0.016f), new Vector2(0.05f, 0.016f));

            BuildArm(-1, out _shoulderL, out _elbowL);
            BuildArm(1, out _shoulderR, out _elbowR);
            BuildLeg(-1, out _hipL, out _kneeL);
            BuildLeg(1, out _hipR, out _kneeR);
        }

        void BuildArm(int side, out Transform shoulder, out Transform elbow)
        {
            string name = side < 0 ? "L" : "R";
            shoulder = Joint("Shoulder " + name, _torso, new Vector3(0.29f * side, 0.47f, 0f));
            Part("Sleeve " + name, shoulder, PixelTextures.Kind.Cloth, new Vector3(0f, -0.13f, 0f), 0.28f, new Vector2(0.105f, 0.105f), new Vector2(0.135f, 0.135f));
            elbow = Joint("Elbow " + name, shoulder, new Vector3(0f, -0.27f, 0f));
            Part("Forearm " + name, elbow, PixelTextures.Kind.Skin, new Vector3(0f, -0.1f, 0f), 0.2f, new Vector2(0.08f, 0.08f), new Vector2(0.09f, 0.09f));
            Part("Bracer " + name, elbow, PixelTextures.Kind.Leather, new Vector3(0f, -0.11f, 0f), 0.11f, new Vector2(0.098f, 0.098f), new Vector2(0.1f, 0.1f));
            Part("Hand " + name, elbow, PixelTextures.Kind.Skin, new Vector3(0f, -0.25f, 0.005f), 0.1f, new Vector2(0.07f, 0.09f), new Vector2(0.08f, 0.1f));
        }

        void BuildLeg(int side, out Transform hip, out Transform knee)
        {
            string name = side < 0 ? "L" : "R";
            hip = Joint("Hip " + name, _hips, new Vector3(0.105f * side, -0.02f, 0f));
            Part("Thigh " + name, hip, PixelTextures.Kind.Trousers, new Vector3(0f, -0.21f, 0f), 0.42f, new Vector2(0.125f, 0.135f), new Vector2(0.155f, 0.165f));
            knee = Joint("Knee " + name, hip, new Vector3(0f, -0.42f, 0f));
            Part("Shin " + name, knee, PixelTextures.Kind.Trousers, new Vector3(0f, -0.17f, 0f), 0.34f, new Vector2(0.105f, 0.115f), new Vector2(0.125f, 0.135f));
            Part("Leg Wrap " + name, knee, PixelTextures.Kind.Leather, new Vector3(0f, -0.25f, 0f), 0.14f, new Vector2(0.115f, 0.125f), new Vector2(0.118f, 0.128f));
            Part("Boot " + name, knee, PixelTextures.Kind.Leather, new Vector3(0f, -0.43f, 0.035f), 0.12f, new Vector2(0.125f, 0.25f), new Vector2(0.12f, 0.17f));
        }

        static Transform Joint(string name, Transform parent, Vector3 localPosition)
        {
            var joint = new GameObject(name).transform;
            joint.SetParent(parent, false);
            joint.localPosition = localPosition;
            return joint;
        }

        /// <summary>A tapered box: <paramref name="bottom"/> and <paramref name="top"/> are (width, depth).</summary>
        void Part(string name, Transform parent, PixelTextures.Kind kind, Vector3 center, float height, Vector2 bottom, Vector2 top)
        {
            _buffers.Clear();
            float h = height * 0.5f;
            var corners = new Vector3[8];
            for (int i = 0; i < 8; i++)
            {
                bool upper = (i & 4) != 0;
                Vector2 size = upper ? top : bottom;
                float x = ((i & 1) != 0 ? 0.5f : -0.5f) * size.x;
                float z = ((i & 2) != 0 ? 0.5f : -0.5f) * size.y;
                corners[i] = new Vector3(x, upper ? h : -h, z);
            }
            // Faces as corner indices (any winding; AddFace orients them outward).
            AddFace(corners, 0, 1, 3, 2); // bottom
            AddFace(corners, 4, 5, 7, 6); // top
            AddFace(corners, 0, 1, 5, 4); // back (-z)
            AddFace(corners, 2, 3, 7, 6); // front (+z)
            AddFace(corners, 0, 2, 6, 4); // left (-x)
            AddFace(corners, 1, 3, 7, 5); // right (+x)

            var mesh = new Mesh { name = name };
            mesh.SetVertices(_buffers.Vertices);
            mesh.SetNormals(_buffers.Normals);
            mesh.SetUVs(0, _buffers.UVs);
            mesh.SetTriangles(_buffers.Indices, 0, true);
            _meshes.Add(mesh);

            var part = new GameObject(name);
            part.transform.SetParent(parent, false);
            part.transform.localPosition = center;
            part.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = part.AddComponent<MeshRenderer>();

            if (!_renderersByKind.TryGetValue(kind, out var list))
            {
                list = new List<Renderer>();
                _renderersByKind.Add(kind, list);
            }
            list.Add(renderer);
        }

        /// <summary>Adds one flat-shaded quad with box-projected UVs, so the pixels are the same size everywhere.</summary>
        void AddFace(Vector3[] corners, int a, int b, int c, int d)
        {
            Vector3 pa = corners[a], pb = corners[b], pc = corners[c], pd = corners[d];
            Vector3 normal = Vector3.Cross(pb - pa, pc - pa).normalized;
            Vector3 center = (pa + pb + pc + pd) * 0.25f;
            if (Vector3.Dot(normal, center) < 0f)
            {
                // Flip so the face points away from the middle of the box (Unity draws the clockwise side).
                (pb, pd) = (pd, pb);
                normal = -normal;
            }

            float scale = 1f / Mathf.Max(0.05f, metersPerTexture);
            int start = _buffers.Vertices.Count;
            foreach (Vector3 p in new[] { pa, pb, pc, pd })
            {
                _buffers.Vertices.Add(p);
                _buffers.Normals.Add(normal);
                Vector3 n = new Vector3(Mathf.Abs(normal.x), Mathf.Abs(normal.y), Mathf.Abs(normal.z));
                Vector2 uv = n.y > n.x && n.y > n.z ? new Vector2(p.x, p.z) : n.x > n.z ? new Vector2(p.z, p.y) : new Vector2(p.x, p.y);
                _buffers.UVs.Add(uv * scale + new Vector2(0.37f, 0.5f));
            }
            _buffers.Indices.Add(start);
            _buffers.Indices.Add(start + 1);
            _buffers.Indices.Add(start + 2);
            _buffers.Indices.Add(start);
            _buffers.Indices.Add(start + 2);
            _buffers.Indices.Add(start + 3);
        }

        // ---------- Animation ----------

        void LateUpdate()
        {
            if (!_built) return;
            float dt = Mathf.Max(Time.deltaTime, 1e-4f);

            // Movement comes from how far the character actually moved, so remote players animate too.
            Vector3 position = transform.position;
            Vector3 delta = position - _lastPosition;
            _lastPosition = position;
            if (delta.sqrMagnitude > 25f) delta = Vector3.zero; // teleported

            float horizontal = new Vector2(delta.x, delta.z).magnitude / dt;
            _speed = Mathf.Lerp(_speed, horizontal, 1f - Mathf.Exp(-dt * 10f));
            _verticalSpeed = Mathf.Lerp(_verticalSpeed, delta.y / dt, 1f - Mathf.Exp(-dt * 10f));
            // In the air = nothing close under the feet (the cast starts inside our own capsule, which it ignores).
            bool grounded = Physics.SphereCast(position + Vector3.up * 0.4f, 0.2f, Vector3.down, out _, 0.5f, ~0, QueryTriggerInteraction.Ignore);
            float airTarget = !grounded || _verticalSpeed > 2f ? 1f : 0f;
            _air = Mathf.MoveTowards(_air, airTarget, dt * 5f);

            const float strideLength = 1.5f;
            _phase += _speed * dt / strideLength * Mathf.PI * 2f;
            if (_phase > Mathf.PI * 200f) _phase -= Mathf.PI * 200f;

            float amount = Mathf.Clamp(_speed / 4.5f, 0f, 1.4f); // 1 = walking, 1.4 = sprinting
            float swing = Mathf.Sin(_phase) * 32f * amount;
            float breathe = Mathf.Sin(Time.time * 1.8f);

            // Walk / run.
            // Positive X swings a leg backward. Knees bend most while the leg swings forward from behind.
            float kneeL = (0.5f + 0.5f * Mathf.Sin(_phase - 0.45f)) * 55f * amount + 4f;
            float kneeR = (0.5f + 0.5f * Mathf.Sin(_phase + Mathf.PI - 0.45f)) * 55f * amount + 4f;
            Quaternion hipL = Quaternion.Euler(swing, 0f, 0f);
            Quaternion hipR = Quaternion.Euler(-swing, 0f, 0f);
            Quaternion shoulderL = Quaternion.Euler(-swing * 0.8f, 0f, -7f - breathe * 0.8f);
            Quaternion shoulderR = Quaternion.Euler(swing * 0.8f, 0f, 7f + breathe * 0.8f);
            float elbow = 12f + 30f * amount;

            // In the air: knees up, arms out.
            hipL = Quaternion.Slerp(hipL, Quaternion.Euler(-28f, 0f, 0f), _air);
            hipR = Quaternion.Slerp(hipR, Quaternion.Euler(-12f, 0f, 0f), _air);
            kneeL = Mathf.Lerp(kneeL, 55f, _air);
            kneeR = Mathf.Lerp(kneeR, 35f, _air);
            shoulderL = Quaternion.Slerp(shoulderL, Quaternion.Euler(-25f, 0f, -38f), _air);
            shoulderR = Quaternion.Slerp(shoulderR, Quaternion.Euler(-25f, 0f, 38f), _air);
            elbow = Mathf.Lerp(elbow, 35f, _air);

            _hipL.localRotation = hipL;
            _hipR.localRotation = hipR;
            _kneeL.localRotation = Quaternion.Euler(kneeL, 0f, 0f);
            _kneeR.localRotation = Quaternion.Euler(kneeR, 0f, 0f);
            _shoulderL.localRotation = shoulderL;
            _shoulderR.localRotation = shoulderR;
            _elbowL.localRotation = Quaternion.Euler(-elbow, 0f, 0f);
            _elbowR.localRotation = Quaternion.Euler(-elbow, 0f, 0f);

            // Body: bob twice per stride, lean into the run, breathe when standing.
            float bob = Mathf.Abs(Mathf.Cos(_phase)) * 0.035f * amount * (1f - _air);
            _hips.localPosition = _hipsRest + Vector3.up * (bob - 0.02f * amount);
            _torso.localRotation = Quaternion.Euler(5f * amount + breathe * 0.8f * Mathf.Clamp01(1f - amount), Mathf.Sin(_phase) * 4f * amount, 0f);
            _head.localRotation = Quaternion.Euler(-4f * amount, -Mathf.Sin(_phase) * 3f * amount, 0f);
        }
    }
}
