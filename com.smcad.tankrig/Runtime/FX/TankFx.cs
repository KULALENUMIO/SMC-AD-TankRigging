// Realistic light effects for a rigged tank. Each one replaces the matching built-in particle effect:
//   gun flashes  - machine guns and secondary cannons: layered flash, device jets, fireball, a little smoke, sparks and a
//                  flicker of light, sized from the calibre (MachineGunSpec.caliberMm / TankSecondaryTurret.caliberMm)
//   exhaust      - short diesel puffs that follow the throttle (a dark puff when it opens), drift with the wind
//   dust         - dust cloud and clods thrown up by the tracks, coloured by the ground under them
//   track spill  - soil carried round on the tracks, falling off the top run and flung off at the front
//   main gun     - flame, fireball, muzzle cloud, ground and shock dust, bore wisp and gravel as light particles with
//                  flipbooks baked from the simulation (MainGunFX, the default), or - for recorded takes - (heavy) the blast
//                  computed on the GPU (MuzzleBlastSim + MuzzleDebris, a few shots alive at once)
//   ruts         - (heavy) the ground really gives under the tracks (TankTrackRuts: deformation field + tessellated ground)
// Nothing collides. The light ones are drawn by one HDRP custom pass (GunFlashPass), the main-gun blast by two
// (MuzzleBlastPass + MuzzleShockPass); they are added to the scene when missing. The heavy ones are made in play mode.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace TankRig.FX
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Tank Rig/Tank FX（リアルなエフェクト）")]
    public class TankFx : MonoBehaviour
    {
        [Tooltip("the tank (empty = the TankController on this object or above)")] public TankController controller;
        [Tooltip("機銃・副砲の発砲（口径で大きさが決まる）")] public bool gunFlashes = true;
        [Tooltip("排気（スロットルに応じて。開けたときは黒い煙）")] public bool exhaust = true;
        [Tooltip("土煙と土塊（地面の色）")] public bool dust = true;
        [Tooltip("土煙の量（1 = 撮影向けの濃さ。少ないほど画面を覆う面積が減って軽い。少ない分は少し濃くする）")] [Range(0.2f, 1f)] public float dustAmount = 0.5f;
        [Tooltip("履帯の巻き込み（土が上を回ってこぼれる）")] public bool trackSpill = true;
        [Header("主砲")]
        [Tooltip("主砲の発砲エフェクト（炎・火球・煙・土煙・小石）")] public bool mainGunBlast = true;
        [Tooltip("主砲の口径（mm）。軽量版の大きさがこれで決まる")] public float mainGunCaliberMm = 120f;
        [Tooltip("主砲の発砲で戦車の周りに小石が飛ぶ")] public bool gravel = true;
        [Header("重いもの（プレイ中に作る）")]
        [Tooltip("撮影用: 主砲の発砲を GPU で計算する（火球・煙・衝撃波・砂塵。1 発あたり GPU 数 ms、メモリ約 160 MB）。オフ = シミュレーションから焼いたフリップブックの軽いパーティクル")] public bool mainGunSimulation = false;
        [Tooltip("同時に残せる主砲の発砲（煙が消える前に次を撃つとき）")] [Range(1, 4)] public int blastSlots = 2;
        [Tooltip("主砲の発砲の品質: 0 高（25 cm の格子）/ 1 中（35 cm）/ 2 低（50 cm）。低いほど GPU が軽い")] [Range(0, 2)] public int blastQuality = 2;
        [Tooltip("履帯の轍（地面が実際にへこむ。地面は細かいメッシュの HDRP/Lit か Unity Terrain）")] public bool ruts = true;
        [Tooltip("shared pass material (Hidden/TankRig/Flash); made from the shader when empty")] public Material flashMaterial;
        [Tooltip("add the shared custom pass to the scene in play mode when there is none")] public bool installPass = true;

        readonly Dictionary<Transform, GunFlashFX> flashes = new Dictionary<Transform, GunFlashFX>();
        MuzzleBlastPool blastPool;
        MainGunFX mainFx;
        int builtQuality = -1;
        TankTrackRuts rutsFx;
        public MuzzleBlastPool MainGunBlast => blastPool;
        /// <summary>The particle main gun (when not simulated).</summary>
        public MainGunFX MainGunParticles => mainFx;
        public TankTrackRuts Ruts => rutsFx;
        TankController subscribed;

        public TankExhaustFX Exhaust => GetComponent<TankExhaustFX>();
        public TankDustFX Dust => GetComponent<TankDustFX>();
        public TankTrackSpillFX Spill => GetComponent<TankTrackSpillFX>();

        void Reset()
        {
            controller = GetComponentInParent<TankController>();
            AddParts();
        }

        /// <summary>Adds the exhaust / dust / spill components to this object when missing (the builder calls this).</summary>
        public void AddParts()
        {
            var tank = controller ? controller.transform : transform;
            if (!GetComponent<TankExhaustFX>()) gameObject.AddComponent<TankExhaustFX>().tank = tank;
            if (!GetComponent<TankDustFX>()) gameObject.AddComponent<TankDustFX>().tank = tank;
            if (!GetComponent<TankTrackSpillFX>()) gameObject.AddComponent<TankTrackSpillFX>().tank = tank;
            // the rig's own exhaust points and tracks, when it has them
            var ex = GetComponent<TankExhaustFX>();
            if (controller && controller.exhausts != null && controller.exhausts.Length > 0 && (ex.outlets == null || ex.outlets.Length == 0)) ex.outlets = controller.exhausts;
            if (controller)
            {
                var tr = new List<Transform>();
                if (controller.leftTrack) tr.Add(controller.leftTrack.transform);
                if (controller.rightTrack) tr.Add(controller.rightTrack.transform);
                if (tr.Count == 2)
                {
                    var d = GetComponent<TankDustFX>(); var sp = GetComponent<TankTrackSpillFX>();
                    if (d.tracks == null || d.tracks.Length == 0) { d.tracks = tr.ToArray(); if (d.isActiveAndEnabled) d.Setup(); }
                    if (sp.tracks == null || sp.tracks.Length == 0) { sp.tracks = tr.ToArray(); if (sp.isActiveAndEnabled) sp.Setup(); }
                }
            }
        }

        void OnEnable()
        {
            if (!controller) controller = GetComponentInParent<TankController>();
            if (Application.isPlaying)
            {
                AddParts();
                if (installPass) EnsurePass(flashMaterial);
                if (mainGunBlast) SetupMainGun();
                if (ruts) SetupRuts();
            }
            Subscribe();
            Apply();
            if (Application.isPlaying && gunFlashes && controller) PrewarmFlashes();
        }

        /// <summary>The flashes of every machine gun and secondary cannon, made now (not at the first round).</summary>
        void PrewarmFlashes()
        {
            foreach (var mg in controller.machineGuns)
                if (mg != null && mg.point) Flash(mg.point, GunKind.MachineGun, mg.spec != null ? mg.spec.caliberMm : 7.62f).Prewarm();
            foreach (var st in controller.secondaryTurrets)
                if (st != null && st.muzzle)
                    Flash(st.muzzle, st.weapon == SecondaryWeapon.Cannon ? GunKind.Autocannon : GunKind.MachineGun,
                          st.weapon == SecondaryWeapon.Cannon ? st.caliberMm : (st.machineGun != null ? st.machineGun.caliberMm : 7.62f)).Prewarm();
        }

        /// <summary>The main gun's effect: light particles, or (mainGunSimulation) the GPU blast.</summary>
        void SetupMainGun()
        {
            if (!controller || !controller.muzzle) return;
            if (mainGunSimulation) SetupMainGunSim(); else SetupMainGunParticles();
        }

        /// <summary>The particle main gun (flipbooks baked from the simulation).</summary>
        void SetupMainGunParticles()
        {
            if (mainFx) return;
            mainFx = gameObject.AddComponent<MainGunFX>();
            mainFx.muzzle = controller.muzzle;
            mainFx.tank = controller.transform;
            mainFx.gunRoot = controller.gun ? controller.gun : controller.barrel;
            mainFx.caliberMm = mainGunCaliberMm;
            mainFx.fx.gravel.enabled = gravel;
            mainFx.Prewarm();
        }

        /// <summary>The GPU blast for the main gun: a pool of shots (real-time preset), gravel, a flash light and its passes.</summary>
        void SetupMainGunSim()
        {
            if (blastPool) return;
            var solver = Resources.Load<ComputeShader>("TankRigFX/MuzzleBlast");
            var march = Shader.Find("Hidden/TankRig/BlastRaymarch");
            if (!solver || !march) { Debug.LogWarning("TankFx: main-gun blast shaders missing"); return; }
            var go = new GameObject($"{controller.name} Main Gun Blast");   // in the world: the smoke stays where it was fired
            var sim = go.AddComponent<MuzzleBlastSim>();
            sim.muzzle = controller.muzzle;
            sim.ignoreRoot = controller.transform;
            sim.obstacleRoot = controller.transform;
            sim.solver = solver;
            sim.dustSolver = Resources.Load<ComputeShader>("TankRigFX/MuzzleDust");
            sim.occSolver = Resources.Load<ComputeShader>("TankRigFX/MuzzleOcc");
            sim.sun = FxScene.FindSun();
            sim.ApplyRealtimePreset(blastQuality);
            builtQuality = blastQuality;
            var lgo = new GameObject("Blast Light");
            lgo.transform.SetParent(go.transform, false);
            var light = lgo.AddComponent<Light>();
            lgo.AddComponent<HDAdditionalLightData>().volumetricDimmer = 1f;   // the flash glows in the haze
            light.type = LightType.Point;
            light.lightUnit = UnityEngine.Rendering.LightUnit.Lumen;
            light.color = new Color(1f, 0.80f, 0.55f);
            light.intensity = 0f; light.range = 120f;
            light.shadows = LightShadows.Soft;
            sim.flashLight = light;
            if (gravel) { var d = go.AddComponent<MuzzleDebris>(); d.sim = sim; d.enabled = false; d.enabled = true; }
            blastPool = new GameObject($"{controller.name} Main Gun Blast Pool").AddComponent<MuzzleBlastPool>();   // apart from the shot it copies
            blastPool.template = sim;
            blastPool.slots = blastSlots;
            foreach (var s in blastPool.Slots)
            {
                s.prewarmOnStart = true;   // compiles and fills the kernels before the first shot
                var deb = s.GetComponent<MuzzleDebris>();
                if (deb) deb.Prewarm();
            }
            EnsureBlastPasses();
        }

        /// <summary>The ruts under this tank's tracks (the ground's materials are swapped while it runs).</summary>
        void SetupRuts()
        {
            if (rutsFx) return;
            rutsFx = gameObject.AddComponent<TankTrackRuts>();
            rutsFx.tank = controller ? controller.transform : transform;
            if (controller && controller.leftTrack && controller.rightTrack) rutsFx.tracks = new[] { controller.leftTrack.transform, controller.rightTrack.transform };
            rutsFx.enabled = false; rutsFx.enabled = true;
        }

        /// <summary>Makes sure some custom pass volume draws the main-gun blast (smoke ray march + blast-wave refraction).</summary>
        public static void EnsureBlastPasses()
        {
            foreach (var v in FindObjectsByType<CustomPassVolume>(FindObjectsSortMode.None))
                foreach (var p in v.customPasses)
                    if (p is MuzzleBlastPass) return;
            var go = new GameObject("Tank Rig Blast Pass");
            var vol = go.AddComponent<CustomPassVolume>();
            vol.isGlobal = true;
            vol.injectionPoint = CustomPassInjectionPoint.BeforePostProcess;
            var pass = (MuzzleBlastPass)vol.AddPassOfType(typeof(MuzzleBlastPass));
            pass.name = "Tank Rig main-gun smoke";
            pass.material = new Material(Shader.Find("Hidden/TankRig/BlastRaymarch"));
            pass.halfResolution = true;   // real time: half resolution + temporal reuse
            pass.temporal = true;
            var shock = (MuzzleShockPass)vol.AddPassOfType(typeof(MuzzleShockPass));   // after the smoke: refracts it too
            shock.name = "Tank Rig blast wave";
            shock.material = new Material(Shader.Find("Hidden/TankRig/BlastShock"));
        }

        void OnDisable()
        {
            Unsubscribe();
            if (controller) { controller.externalGunFlashes = false; controller.externalExhaust = false; controller.externalMainGun = false; }
            if (blastPool) { foreach (var sl in blastPool.Slots) if (sl) Destroy(sl.gameObject); Destroy(blastPool.gameObject); }   // the shots and the pool
            if (mainFx) Destroy(mainFx);
            if (rutsFx) Destroy(rutsFx);
            blastPool = null; mainFx = null; rutsFx = null;
        }

        void OnValidate()
        {
            if (!isActiveAndEnabled) return;
            Apply();
            // changed in the inspector while playing: rebuild the main-gun blast at the new quality
            if (Application.isPlaying && blastPool && builtQuality != blastQuality) SetBlastQuality(blastQuality);
            if (Application.isPlaying && mainGunBlast && (mainGunSimulation ? !blastPool : !mainFx) && (blastPool || mainFx)) SetMainGunSimulation(mainGunSimulation);
        }

        /// <summary>Switch the main-gun blast between low / medium / high while playing (e.g. high for a recorded take).
        /// The shots alive now are dropped; the next shot uses the new quality.</summary>
        public void SetBlastQuality(int quality)
        {
            blastQuality = Mathf.Clamp(quality, 0, 2);
            if (!Application.isPlaying || !mainGunBlast || !mainGunSimulation) return;
            if (blastPool) { foreach (var sl in blastPool.Slots) if (sl) Destroy(sl.gameObject); Destroy(blastPool.gameObject); blastPool = null; }
            SetupMainGun();
            Apply();
        }

        /// <summary>Switch the main gun between the light particles and the GPU simulation while playing (the simulation
        /// for a recorded take). The effects alive now are dropped.</summary>
        public void SetMainGunSimulation(bool on)
        {
            mainGunSimulation = on;
            if (!Application.isPlaying || !mainGunBlast) return;
            if (blastPool) { foreach (var sl in blastPool.Slots) if (sl) Destroy(sl.gameObject); Destroy(blastPool.gameObject); blastPool = null; }
            if (mainFx) { Destroy(mainFx); mainFx = null; }
            SetupMainGun();
            Apply();
        }

        void Subscribe()
        {
            if (subscribed == controller) return;
            Unsubscribe();
            if (!controller) return;
            controller.GunFired += OnGunFired;
            controller.RoundFired += OnRoundFired;
            subscribed = controller;
        }

        void Unsubscribe()
        {
            if (!subscribed) { subscribed = null; return; }
            subscribed.GunFired -= OnGunFired;
            subscribed.RoundFired -= OnRoundFired;
            subscribed = null;
        }

        /// <summary>Switches the parts on / off and tells the controller which built-in effects to leave out.</summary>
        public void Apply()
        {
            if (controller) { controller.externalGunFlashes = gunFlashes; controller.externalExhaust = exhaust && Exhaust; controller.externalMainGun = blastPool || mainFx; }
            if (Exhaust) Exhaust.enabled = exhaust;
            if (Dust) { Dust.enabled = dust; Dust.amountScale = dustAmount; }
            if (Spill) Spill.enabled = trackSpill;
            foreach (var f in flashes.Values) if (f) f.enabled = gunFlashes;
        }

        void OnRoundFired(TankController c, TankController.RoundShot shot)
        {
            if (!gunFlashes || !shot.point) return;
            Flash(shot.point, GunKind.MachineGun, shot.spec != null ? shot.spec.caliberMm : 7.62f).Shot();
        }

        void OnGunFired(TankController c, TankController.GunShot shot)
        {
            if (shot.secondary == null) { if (blastPool) blastPool.Fire(); else if (mainFx) mainFx.Shot(); return; }   // main gun: ours (else the built-in effect)
            if (!gunFlashes || !shot.muzzle) return;
            Flash(shot.muzzle, GunKind.Autocannon, shot.secondary.caliberMm).Shot();
        }

        /// <summary>The flash for a muzzle point (made on first use, re-sized when its gun kind or calibre changes).</summary>
        public GunFlashFX Flash(Transform point, GunKind kind, float caliberMm)
        {
            if (!flashes.TryGetValue(point, out var f) || !f)
            {
                f = point.GetComponent<GunFlashFX>();
                if (!f) f = point.gameObject.AddComponent<GunFlashFX>();   // made in play mode only (shots), never saved
                f.muzzle = point;
                f.ApplyPreset(kind, caliberMm);
                flashes[point] = f;
            }
            else if (f.profile.kind != kind || !Mathf.Approximately(f.profile.caliberMm, caliberMm)) f.ApplyPreset(kind, caliberMm);
            return f;
        }

        /// <summary>Makes sure some custom pass volume in the scene draws the flash particles (a global one is added if not).</summary>
        public static GunFlashPass EnsurePass(Material material)
        {
            foreach (var v in FindObjectsByType<CustomPassVolume>(FindObjectsSortMode.None))
                foreach (var p in v.customPasses)
                    if (p is GunFlashPass gp) { if (!gp.material && material) gp.material = material; return gp; }
            var go = new GameObject("Tank Rig FX Pass");
            var vol = go.AddComponent<CustomPassVolume>();
            vol.isGlobal = true;
            vol.injectionPoint = CustomPassInjectionPoint.BeforePostProcess;
            var pass = (GunFlashPass)vol.AddPassOfType(typeof(GunFlashPass));
            pass.name = "Tank Rig flashes";
            pass.material = material;
            return pass;
        }
    }
}
