using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace TankRig
{
    /// <summary>One frame of player input (keyboard + mouse).</summary>
    [System.Serializable]
    public struct TankInputState
    {
        public float throttle;      // W / S
        public float steer;         // A / D
        public Vector2 look;        // mouse delta (pixels)
        public float scroll;        // mouse wheel (notches)
        public bool fireMain;       // left click (this frame)
        public bool holdMain;       // left button (held): machine guns on the main gun's button
        public bool fireMachineGun; // Space (held)
        public bool fireSecondary;  // middle button or F (held): secondary weapons
        public bool aim;            // right mouse (held): zoom
        public bool toggleLights;   // L (this frame)
        public bool unlockCursor;   // Esc (this frame)
        public bool click;          // left click (this frame), used to lock the cursor again
        public bool cycleView;      // V (this frame): third person / sights
        public bool lase;           // R (this frame): laser rangefinder
        public bool sensorMode;     // T (this frame): sight camera (thermal / day)
        public bool polarity;       // B (this frame): white hot / black hot
        public bool illumination;   // N (this frame): reticle illumination
        public bool designate;      // G (this frame): designate / release a target (helmet sight)
        public int ammoSelect;      // 1-4 (this frame), 0 = none
    }

    /// <summary>Something that tells the tank where to aim instead of its camera (a sight).</summary>
    public interface ITankAimSource
    {
        bool HasAim { get; }
        Vector3 AimPoint { get; }
    }

    public static class TankInput
    {
        public static TankInputState Read()
        {
            var s = new TankInputState();
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current;
            var m = Mouse.current;
            if (k != null)
            {
                s.throttle = (k.wKey.isPressed ? 1f : 0f) - (k.sKey.isPressed ? 1f : 0f);
                s.steer = (k.dKey.isPressed ? 1f : 0f) - (k.aKey.isPressed ? 1f : 0f);
                s.fireMachineGun = k.spaceKey.isPressed;
                s.fireSecondary = k.fKey.isPressed;
                s.toggleLights = k.lKey.wasPressedThisFrame;
                s.unlockCursor = k.escapeKey.wasPressedThisFrame;
                s.cycleView = k.vKey.wasPressedThisFrame;
                s.lase = k.rKey.wasPressedThisFrame;
                s.sensorMode = k.tKey.wasPressedThisFrame;
                s.polarity = k.bKey.wasPressedThisFrame;
                s.illumination = k.nKey.wasPressedThisFrame;
                s.designate = k.gKey.wasPressedThisFrame;
                s.ammoSelect = k.digit1Key.wasPressedThisFrame ? 1 : k.digit2Key.wasPressedThisFrame ? 2
                    : k.digit3Key.wasPressedThisFrame ? 3 : k.digit4Key.wasPressedThisFrame ? 4 : 0;
            }
            if (m != null)
            {
                s.look = m.delta.ReadValue();
                // Windows reports 120 per wheel notch, other platforms / settings about 1
                float sy = m.scroll.ReadValue().y;
                s.scroll = Mathf.Abs(sy) >= 10f ? sy / 120f : sy;
                s.fireMain = m.leftButton.wasPressedThisFrame;
                s.holdMain = m.leftButton.isPressed;
                s.fireSecondary |= m.middleButton.isPressed;
                s.click = s.fireMain;
                s.aim = m.rightButton.isPressed;
            }
#elif ENABLE_LEGACY_INPUT_MANAGER
            s.throttle = (Input.GetKey(KeyCode.W) ? 1f : 0f) - (Input.GetKey(KeyCode.S) ? 1f : 0f);
            s.steer = (Input.GetKey(KeyCode.D) ? 1f : 0f) - (Input.GetKey(KeyCode.A) ? 1f : 0f);
            s.fireMachineGun = Input.GetKey(KeyCode.Space);
            s.fireSecondary = Input.GetKey(KeyCode.F) || Input.GetMouseButton(2);
            s.holdMain = Input.GetMouseButton(0);
            s.toggleLights = Input.GetKeyDown(KeyCode.L);
            s.unlockCursor = Input.GetKeyDown(KeyCode.Escape);
            s.cycleView = Input.GetKeyDown(KeyCode.V);
            s.lase = Input.GetKeyDown(KeyCode.R);
            s.sensorMode = Input.GetKeyDown(KeyCode.T);
            s.polarity = Input.GetKeyDown(KeyCode.B);
            s.illumination = Input.GetKeyDown(KeyCode.N);
            s.designate = Input.GetKeyDown(KeyCode.G);
            s.ammoSelect = Input.GetKeyDown(KeyCode.Alpha1) ? 1 : Input.GetKeyDown(KeyCode.Alpha2) ? 2
                : Input.GetKeyDown(KeyCode.Alpha3) ? 3 : Input.GetKeyDown(KeyCode.Alpha4) ? 4 : 0;
            s.look = new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y")) * 10f;
            s.scroll = Input.mouseScrollDelta.y;
            s.fireMain = Input.GetMouseButtonDown(0);
            s.click = s.fireMain;
            s.aim = Input.GetMouseButton(1);
#endif
            return s;
        }
    }
}
