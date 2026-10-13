using System.Collections.Generic;
using System.Reflection;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedVR.VR
{
    /// <summary>
    /// The game's HUD widgets that belong to a place in the world, hung in the world instead of on the flat HUD panel:
    /// the teammates' name labels (PlayerGroupUI) and the hit markers. Both are placed by the game with
    /// MainCamera.WorldToViewportPoint, which on the HUD panel only lines up with the disabled flat camera.
    /// The game keeps deciding everything (who gets a label: Group_HUD config, same group, within 512 m, behind-camera
    /// test, nickname, alpha fade near the centre; hit markers: config, style, animation, lifetime). This class only
    /// reads what the game shows: for each visible widget it crops that widget's rectangle out of the UI texture (the same
    /// way the hint panel does) onto a quad at the widget's world anchor (the teammate's head spot, the hit point),
    /// turned towards the eye, scaled with distance so it keeps the screen size it has on the flat game. VRGui cuts the
    /// same rectangles out of the HUD panel (the rects are added to the holes list) so each shows only once.
    /// No depth test (VRGui's overlay camera), so teammates' names show through walls, like the game's own labels.
    /// All numbers are GUESSES; tune by feel. Cost: reflection lookups once, then a few vector operations per widget.
    /// </summary>
    internal sealed class VRWorldLabels
    {
        // The game anchors the label 3 m above the player model's position (PlayerUI.updateGroupLabels).
        private const float GroupLabelHeight = 3f;
        // Apparent size relative to the same widget on the HUD panel (which is small at 2 m): GUESS.
        private const float SizeScale = 2f;
        private const float LabelMinDistance = 1.5f;     // closer than this the label stops shrinking
        private const float HitmarkerMinDistance = 2f;
        private const float MaxDistance = 700f;
        private const float Pad = 4f;                    // canvas units around the widget (its text outline / shadow)

        private const BindingFlags AnyMember = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        // Internal members of the game, read by reflection (resolved once).
        private static readonly FieldInfo playerUiInstance = typeof(PlayerUI).GetField("instance", AnyMember);
        private static readonly FieldInfo groupUiField = typeof(PlayerUI).GetField("groupUI", AnyMember);
        private static readonly FieldInfo activeHitmarkers = typeof(PlayerLifeUI).GetField("activeHitmarkers", AnyMember);
        private static PropertyInfo groupsProperty;
        private static bool loggedMissing;

        private sealed class Piece
        {
            public GameObject Object;
            public Transform Transform;
            public Mesh Mesh;
            public readonly Vector2[] Uv = new Vector2[4];
        }

        private readonly Transform parent;
        private readonly Material material;
        private readonly int layer;
        private readonly List<Piece> pieces = new List<Piece>();
        private int used;
        private bool loggedLabel;
        private bool loggedHitmarker;

        // Per update
        private Transform eye;
        private Vector2 canvasSize;
        private float textureAspect;
        private float widthPerMeter;
        private List<Rect> holes;

        public VRWorldLabels(Transform parent, Material material, int layer)
        {
            this.parent = parent;
            this.material = material;
            this.layer = layer;
        }

        /// <summary>Number of widgets currently hung in the world.</summary>
        public int Count => used;

        /// <summary>Hides every quad (menu, lens, HUD closed).</summary>
        public void Hide()
        {
            for (int i = 0; i < pieces.Count; i++)
            {
                if (pieces[i].Object.activeSelf)
                {
                    pieces[i].Object.SetActive(false);
                }
            }
            used = 0;
        }

        /// <summary>
        /// Once per frame, after the game's HUD update (LateUpdate). Adds the canvas rectangle (origin top left) of every
        /// widget it hung in the world to <paramref name="holeRects"/>.
        /// </summary>
        /// <param name="widthPerMeter">HUD panel width per metre of distance (the panel's width / its distance).</param>
        public void Update(Transform eyeTransform, Vector2 canvas, float aspect, float widthPerMeter, List<Rect> holeRects)
        {
            eye = eyeTransform;
            canvasSize = canvas;
            textureAspect = Mathf.Max(aspect, 0.1f);
            this.widthPerMeter = widthPerMeter;
            holes = holeRects;
            used = 0;

            if (eye != null && HudShown())
            {
                AddGroupLabels();
                AddHitmarkers();
            }

            for (int i = used; i < pieces.Count; i++)
            {
                if (pieces[i].Object.activeSelf)
                {
                    pieces[i].Object.SetActive(false);
                }
            }
        }

        // The widgets live under PlayerUI.container (labels) and PlayerUI.window (hit markers); both are hidden by the
        // game for the binoculars mask, a closed HUD (F-key) and the like.
        private static bool HudShown()
        {
            return PlayerUI.window != null && PlayerUI.window.isEnabled && PlayerUI.container != null && PlayerUI.container.IsVisible;
        }

        private void AddGroupLabels()
        {
            // Teammate labels are for other players: alone on the server (or in singleplayer) there is nothing to show, so
            // the reflection reads below are skipped.
            List<SteamPlayer> players = Provider.clients;
            if (players == null || players.Count <= 1)
            {
                return;
            }

            object ui = playerUiInstance != null ? playerUiInstance.GetValue(null) : null;
            object group = ui != null && groupUiField != null ? groupUiField.GetValue(ui) : null;
            if (group == null)
            {
                if (!loggedMissing && (playerUiInstance == null || groupUiField == null))
                {
                    loggedMissing = true;
                    VRLog.Warn("world labels: PlayerUI.instance / groupUI not found, teammate names stay on the HUD panel.");
                }
                return;
            }

            if (groupsProperty == null)
            {
                groupsProperty = group.GetType().GetProperty("groups", AnyMember);
            }
            List<ISleekLabel> labels = groupsProperty != null ? groupsProperty.GetValue(group, null) as List<ISleekLabel> : null;
            List<SteamPlayer> clients = Provider.clients;
            if (labels == null || clients == null)
            {
                return;
            }

            // Same index pairing the game uses: label i belongs to Provider.clients[i].
            int count = Mathf.Min(labels.Count, clients.Count);
            for (int i = 0; i < count; i++)
            {
                ISleekLabel label = labels[i];
                SteamPlayer client = clients[i];
                if (label == null || !label.IsVisible || client == null || client.model == null)
                {
                    continue;
                }

                if (AddPiece(RectOf(label), client.model.position + Vector3.up * GroupLabelHeight, LabelMinDistance) && !loggedLabel)
                {
                    loggedLabel = true;
                    VRLog.Info("world labels: a teammate name now hangs above the player in the world instead of on the HUD panel.");
                }
            }
        }

        private void AddHitmarkers()
        {
            List<HitmarkerInfo> markers = activeHitmarkers != null ? activeHitmarkers.GetValue(null) as List<HitmarkerInfo> : null;
            if (markers == null)
            {
                return;
            }

            for (int i = 0; i < markers.Count; i++)
            {
                HitmarkerInfo info = markers[i];
                SleekHitmarker element = info.sleekElement;
                if (element == null || !element.IsVisible)
                {
                    continue;
                }

                // Always at the hit point (the game's own setting only picks that or the screen centre, which has no
                // meaning with a head that looks anywhere): the element's content is relative to its own rectangle.
                if (AddPiece(RectOf(element), info.worldPosition, HitmarkerMinDistance) && !loggedHitmarker)
                {
                    loggedHitmarker = true;
                    VRLog.Info("world labels: a hit marker now shows at the hit point in the world instead of on the HUD panel.");
                }
            }
        }

        private Rect RectOf(ISleekElement element)
        {
            float left = element.PositionScale_X * canvasSize.x + element.PositionOffset_X;
            float top = element.PositionScale_Y * canvasSize.y + element.PositionOffset_Y;
            float width = element.SizeScale_X * canvasSize.x + element.SizeOffset_X;
            float height = element.SizeScale_Y * canvasSize.y + element.SizeOffset_Y;
            return Rect.MinMaxRect(left - Pad, top - Pad, left + width + Pad, top + height + Pad);
        }

        private bool AddPiece(Rect canvasRect, Vector3 anchor, float minDistance)
        {
            float u0 = Mathf.Clamp01(canvasRect.xMin / canvasSize.x);
            float u1 = Mathf.Clamp01(canvasRect.xMax / canvasSize.x);
            float v0 = Mathf.Clamp01(1f - canvasRect.yMax / canvasSize.y);
            float v1 = Mathf.Clamp01(1f - canvasRect.yMin / canvasSize.y);
            if (u1 <= u0 || v1 <= v0)
            {
                return false; // entirely off the canvas
            }

            Piece piece = GetPiece(used++);
            // The mesh is only re-uploaded when the cropped rectangle moved (Uv[0] / Uv[3] are its two corners; a fresh
            // piece holds zeros, which a drawn rectangle never equals).
            Vector2 uvMin = new Vector2(u0, v0);
            Vector2 uvMax = new Vector2(u1, v1);
            if (piece.Uv[0] != uvMin || piece.Uv[3] != uvMax)
            {
                piece.Uv[0] = uvMin;
                piece.Uv[1] = new Vector2(u1, v0);
                piece.Uv[2] = new Vector2(u0, v1);
                piece.Uv[3] = uvMax;
                piece.Mesh.uv = piece.Uv;
            }

            Vector3 fromEye = anchor - eye.position;
            float distance = fromEye.magnitude;
            Quaternion rotation = distance > 0.01f ? Quaternion.LookRotation(fromEye / distance, Vector3.up) : eye.rotation;
            float scale = Mathf.Clamp(distance, minDistance, MaxDistance) * widthPerMeter * SizeScale;
            piece.Transform.SetPositionAndRotation(anchor, rotation);
            piece.Transform.localScale = new Vector3((u1 - u0) * scale, (v1 - v0) * scale / textureAspect, 1f);
            if (!piece.Object.activeSelf)
            {
                piece.Object.SetActive(true);
            }

            holes.Add(canvasRect);
            return true;
        }

        private Piece GetPiece(int index)
        {
            while (pieces.Count <= index)
            {
                GameObject go = new GameObject("UnturnedVR GUI World Label");
                go.layer = layer;
                go.transform.SetParent(parent, worldPositionStays: false);

                // Unit quad visible from -Z, like VRGui's panels.
                Mesh mesh = new Mesh { name = "UnturnedVR GUI World Label Quad" };
                mesh.vertices = new[]
                {
                    new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                    new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f),
                };
                mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one };
                mesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };
                mesh.bounds = new Bounds(Vector3.zero, new Vector3(1f, 1f, 0.1f));

                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                MeshRenderer renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                go.SetActive(false);

                pieces.Add(new Piece { Object = go, Transform = go.transform, Mesh = mesh });
            }
            return pieces[index];
        }
    }
}
