using RealLife.Simulation;
using UnityEngine;

namespace RealLife.Runtime
{
    internal sealed class FirstPersonMotor
    {
        private const float WalkSpeed = 3.3f;
        public const float MouseSensitivity = 0.11f;
        private readonly CharacterController controller;
        private readonly Camera camera;
        private float verticalSpeed;

        public FirstPersonMotor(CharacterController controller, Camera camera)
        {
            this.controller = controller;
            this.camera = camera;
        }

        public float Yaw { get; private set; }
        public float Pitch { get; private set; }
        public Vector3 Position => controller.transform.position;
        public Ray Sight => new Ray(camera.transform.position, camera.transform.forward);
        public Vector3 PredictHorizontal(Vector2 movement, Vector2 look, float seconds)
        {
            var walk = Vector2.ClampMagnitude(movement, 1) * WalkSpeed * seconds;
            var heading = Quaternion.Euler(0, Yaw + look.x * MouseSensitivity, 0);
            return Position + heading * new Vector3(walk.x, 0, walk.y);
        }

        public void Drive(Vector2 movement, Vector2 look, float seconds)
        {
            Look(Yaw + look.x * MouseSensitivity, Pitch - look.y * MouseSensitivity);
            var walk = Vector2.ClampMagnitude(movement, 1) * WalkSpeed;
            var displacement = controller.transform.TransformDirection(walk.x, 0, walk.y);
            verticalSpeed = controller.isGrounded ? -1.8f : Mathf.Max(-18, verticalSpeed - 19.6f * seconds);
            displacement.y = verticalSpeed;
            controller.Move(displacement * seconds);
        }

        public void Restore(LocalPoint position, float yaw, float pitch)
        {
            controller.enabled = false;
            controller.transform.position = ToVector(position);
            Look(yaw, pitch);
            controller.enabled = true;
            verticalSpeed = 0;
        }

        private void Look(float yaw, float pitch)
        {
            Yaw = Mathf.Repeat(yaw, 360);
            if (Yaw >= 360) Yaw = 0;
            Pitch = Mathf.Clamp(pitch, -85, 85);
            controller.transform.rotation = Quaternion.Euler(0, Yaw, 0);
            camera.transform.localRotation = Quaternion.Euler(Pitch, 0, 0);
        }

        public static Vector3 ToVector(LocalPoint point) => new Vector3((float)point.X, (float)point.Y, (float)point.Z);
        public static LocalPoint ToLocal(Vector3 point) => new LocalPoint(point.x, point.y, point.z);
    }
}
