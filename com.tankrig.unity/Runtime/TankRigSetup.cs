using System.Collections.Generic;
using UnityEngine;

namespace TankRig
{
    /// <summary>
    /// Authoring data of the builder window, kept on the tank's root: which objects are hull / turret / gun,
    /// the track placement (left side; the right side is mirrored) and the effect points.
    /// In edit mode it keeps the mirrored objects in sync. It does nothing while playing.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("Tank Rig/Tank Rig Setup")]
    public class TankRigSetup : MonoBehaviour
    {
        [Header("モデル")]
        public Transform model;
        public Transform hull;
        public Transform turret;
        [Tooltip("砲座（必須）: 上下（俯仰）に動く部分")] public Transform gun;
        [Tooltip("砲身（なくても可）: 発砲時に前後（後退）する部分。砲座の子にする。ないときは後退しない")] public Transform barrel;
        public Vector3 turretAxisLocal = Vector3.up;
        [Tooltip("砲座の俯仰軸（砲座のローカル）")] public Vector3 gunAxisLocal = Vector3.right;
        [Tooltip("回転軸を SMC-AD のジョイントから取った")] public bool axesFromJoints;

        [Header("履帯")]
        public GameObject trackAsset;
        public GameObject linkAsset;
        public Transform trackLeft;
        public Transform trackRight;
        [Tooltip("左の履帯の位置（x = 中心からの距離、右は左右対称）")] public Vector3 trackOffset = new Vector3(1.5f, 0f, 0f);
        public float trackScale = 1f;
        [Tooltip("履帯の厚さ（車軸方向の倍率）")] public float trackThickness = 1f;
        [Tooltip("履帯モデルの外側が +Z")] public bool trackOuterIsPositiveZ = true;
        public float linkPitch = 0.25f;
        public float linkPitchLineOffset = 0.03125f;
        [Tooltip("リンクのピン軸から接地面までの距離")] public float linkGroundDepth = 0.094f;

        [Header("ポイント")]
        public Transform muzzle;
        [Tooltip("旧形式（v0.6 まで）の機銃。組み立てウィンドウで開くと「機銃」の一覧に移ります")] [HideInInspector] public Transform machineGun;
        public Transform exhaust;
        public Transform headlight;
        public Transform rearLight;
        public Transform exhaustMirror;
        public Transform headlightMirror;
        public Transform rearLightMirror;
        public bool mirrorExhaust = true;
        public bool mirrorHeadlight = true;
        public bool mirrorRearLight = true;
        [HideInInspector] public bool machineGunFollowsGun = true;

        [Header("武装（機銃・副砲塔）")]
        public List<TankMachineGun> machineGuns = new List<TankMachineGun>();
        public List<TankSecondaryTurret> secondaryTurrets = new List<TankSecondaryTurret>();

        [Header("スナップ")]
        [Tooltip("Scrap Mechanic のブロック 1 個の大きさ（m）")] public float blockSize = 0.25f;

        [Header("サスペンション（トーションバー）※m・度")]
        public bool suspension = true;
        [Tooltip("荷重がないときに転輪が下がる量（m）")] public float suspensionDroop = 0.08f;
        [Tooltip("押されたときに転輪が上がる量（m）")] public float suspensionBump = 0.28f;
        [Tooltip("アームの長さ（m）。支点は転輪の後ろ側")] public float armLength = 0.45f;
        [Tooltip("静止時のアームの角度（度）")] public float armAngle = 12f;
        [Tooltip("履帯の上側のたわみの上限（m）")] public float maxSag = 0.12f;
        [Tooltip("車体の揺れの強さ")] public float bodySway = 1f;

        [Header("物理演算")]
        [Tooltip("車体を物理演算で動かす（履帯は描画だけ）")] public bool physicsDrive = true;
        [Tooltip("リアルなエフェクト（機銃・副砲の発砲、排気、土煙、履帯の巻き込み）を付ける。主砲は従来のエフェクト")] public bool realisticEffects = true;
        [Tooltip("車重（トン）")] public float massTons = 40f;
        [Tooltip("車体の当たり判定に使うメッシュ（「戦車を完成させる」で作られるアセット）")] public Mesh hullColliderMesh;

        [Header("走行・武装")]
        public float maxSpeedKmh = 40f;
        public float reverseSpeedKmh = 15f;
        [Tooltip("加速（m/s²）")] public float acceleration = 5f;
        [Tooltip("ブレーキ（m/s²）。キーを離すとこの強さで止まる（実際は摩擦が上限）")] public float braking = 12f;
        [Tooltip("操作の反応（1 = 標準、大きいほどキビキビ）")] public float responsiveness = 1f;
        public float turnRate = 38f;
        public float traverseSpeed = 30f;
        public float minElevation = -8f;
        public float maxElevation = 20f;
        public float reloadTime = 3f;
        [Tooltip("主砲火力（1 = 標準）。反動・のけぞり、砲身の後退量、カメラの揺れ、発砲炎の大きさに比例")] public float gunPower = 1f;

        void Update()
        {
            if (!Application.isPlaying) SyncMirrors();
        }

        public void ApplyTrackPose()
        {
            var rot = Quaternion.Euler(0f, -90f, 0f); // track model: length along +X, axle along Z
            float s = Mathf.Max(0.01f, trackScale);
            float zs = (trackOuterIsPositiveZ ? 1f : -1f) * Mathf.Max(0.05f, trackThickness);
            if (trackLeft) SetLocal(trackLeft, new Vector3(-trackOffset.x, trackOffset.y, trackOffset.z), rot, new Vector3(s, s, s * zs));
            if (trackRight) SetLocal(trackRight, new Vector3(trackOffset.x, trackOffset.y, trackOffset.z), rot, new Vector3(s, s, -s * zs));
        }

        public void SyncMirrors()
        {
            if (trackLeft)
            {
                // follow the left track if it was moved with the normal move / scale tools
                var lp = trackLeft.localPosition;
                trackOffset = new Vector3(-lp.x, lp.y, lp.z);
                trackScale = Mathf.Abs(trackLeft.localScale.x);
            }
            ApplyTrackPose();
            Mirror(exhaust, exhaustMirror, mirrorExhaust);
            Mirror(headlight, headlightMirror, mirrorHeadlight);
            Mirror(rearLight, rearLightMirror, mirrorRearLight);
        }

        void Mirror(Transform src, Transform dst, bool on)
        {
            if (!on || !src || !dst) return;
            var root = transform;
            Vector3 p = root.InverseTransformPoint(src.position); p.x = -p.x;
            Vector3 f = root.InverseTransformDirection(src.forward); f.x = -f.x;
            Vector3 u = root.InverseTransformDirection(src.up); u.x = -u.x;
            Vector3 wp = root.TransformPoint(p);
            Quaternion wr = root.rotation * Quaternion.LookRotation(f, u);
            if ((dst.position - wp).sqrMagnitude > 1e-10f || Quaternion.Angle(dst.rotation, wr) > 0.01f)
                dst.SetPositionAndRotation(wp, wr);
        }

        static void SetLocal(Transform t, Vector3 pos, Quaternion rot, Vector3 scale)
        {
            if ((t.localPosition - pos).sqrMagnitude > 1e-12f) t.localPosition = pos;
            if (Quaternion.Angle(t.localRotation, rot) > 0.001f) t.localRotation = rot;
            if ((t.localScale - scale).sqrMagnitude > 1e-12f) t.localScale = scale;
        }
    }
}
