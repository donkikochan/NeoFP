﻿using UnityEngine;
using UnityEngine.Serialization;
using NeoSaveGames.Serialization;
using NeoSaveGames;
using System.Collections;

namespace NeoFPS.ModularFirearms
{
	[HelpURL("https://docs.neofps.com/manual/weaponsref-mb-hitscanshooter.html")]
	public class HitscanShooter : BaseShooterBehaviour, IUseCameraAim
    {
        [Header("Shooter Settings")]

        [SerializeField, Tooltip("The maximum distance that the weapon will register a hit.")]
		private float m_MaxDistance = 1000f;

        [SerializeField, NeoObjectInHierarchyField(true, required = true), Tooltip("The transform that the bullet actually fires from.")]
        private Transform m_MuzzleTip = null;

        [SerializeField, Tooltip("The layers bullets will collide with.")]
        private LayerMask m_Layers = PhysicsFilter.Masks.BulletBlockers;

        [SerializeField, Tooltip("Should the shot be tested against trigger colliders.")]
        private bool m_QueryTriggerColliders = false;

        [SerializeField, Tooltip("The minimum accuracy spread (in degrees) of the weapon.")]
        private float m_MinimumSpread = 0f;

        [SerializeField, Tooltip("The maximum accuracy spread (in degrees) of the weapon.")]
		private float m_MaximumSpread = 5f;
                
        [FormerlySerializedAs("m_UseCameraForward")] // Remove this
        [SerializeField, Tooltip("When set to use camera aim, the gun first casts from the FirstPersonCamera's aim transform, and then from the muzzle tip to that point to get more accurate firing.")]
        private UseCameraAim m_UseCameraAim = UseCameraAim.HipFireOnly;

        [Header ("Tracer")]

        [SerializeField, NeoPrefabField(typeof(IPooledHitscanTrail)), Tooltip("The optional pooled tracer prototype to use (must implement the IPooledHitscanTrail interface)")]
        private PooledObject m_TracerPrototype = null;

        [SerializeField, Tooltip("How size (thickness/radius) of the tracer line")]
        private float m_TracerSize = 0.05f;

        [SerializeField, Tooltip("How long the tracer line will last")]
		private float m_TracerDuration = 0.05f;
        
        private RaycastHit m_Hit = new RaycastHit();
        private WaitForEndOfFrame m_WaitForEndOfFrame = new WaitForEndOfFrame();

#if UNITY_EDITOR
        protected void OnValidate()
        {
            if (m_MaxDistance < 0.5f)
                m_MaxDistance = 0.5f;
            if (m_TracerDuration < 0f)
                m_TracerDuration = 0f;
            m_TracerSize = Mathf.Clamp(m_TracerSize, 0.001f, 0.25f);
            m_MinimumSpread = Mathf.Clamp(m_MinimumSpread, 0f, 45f);
            m_MaximumSpread = Mathf.Clamp(m_MaximumSpread, 0f, 45f);
        }
        #endif

        public LayerMask collisionLayers
        {
            get { return m_Layers; }
            set { m_Layers = value; }
        }

        public override bool isModuleValid
        {
            get { return m_MuzzleTip != null && m_Layers != 0; }
        }

        public UseCameraAim useCameraAim
        {
            get { return m_UseCameraAim; }
            set { m_UseCameraAim = value; }
        }

        protected virtual float GetModifiedSpread(float unmodified)
        {
            return unmodified;
        }

        public override void Shoot (float accuracy, IAmmoEffect effect)
		{
			// Just return if there is no effect
			if (effect == null)
				return;

            // Get root game object to prevent impacts with body
            Transform ignoreRoot = GetRootTransform();
            //if (firearm.wielder != null)
            //    ignoreRoot = firearm.wielder.gameObject.transform;

            // Get the forward vector
			Vector3 muzzlePosition = m_MuzzleTip.position;
            Vector3 startPosition = muzzlePosition;
            Vector3 forwardVector = m_MuzzleTip.forward;

            bool useCamera = false;
            if (firearm.wielder != null)
            {
                switch (m_UseCameraAim)
                {
                    case UseCameraAim.HipAndAimDownSights:
                        useCamera = true;
                        break;
                    case UseCameraAim.AimDownSightsOnly:
                        if (firearm.aimer != null)
                            useCamera = firearm.aimer.isAiming;
                        break;
                    case UseCameraAim.HipFireOnly:
                        if (firearm.aimer != null)
                            useCamera = !firearm.aimer.isAiming;
                        else
                            useCamera = true;
                        break;
                }
            }
            if (useCamera)
            {
                Transform aimTransform = firearm.wielder.fpCamera.aimTransform;
                startPosition = aimTransform.position;
                forwardVector = aimTransform.forward;
            }

            // Get the direction (with accuracy offset)
            Vector3 rayDirection = forwardVector;
            float spread = GetModifiedSpread(Mathf.Lerp(m_MinimumSpread, m_MaximumSpread, 1f - accuracy));
            if (spread > 0.0001f)
            {
                Quaternion randomRot = UnityEngine.Random.rotationUniform;
                rayDirection = Quaternion.Slerp(Quaternion.identity, randomRot, spread / 360f) * forwardVector;
            }

            // Check for raycast hit
            var queryTriggers = m_QueryTriggerColliders ? QueryTriggerInteraction.Collide : QueryTriggerInteraction.Ignore;
            Ray ray = new Ray(startPosition, rayDirection);
            Vector3 hitPoint;
            bool didHit = PhysicsExtensions.RaycastNonAllocSingle(ray, out m_Hit, m_MaxDistance, m_Layers, ignoreRoot, queryTriggers);
            if (didHit)
                hitPoint = m_Hit.point;
            else
                hitPoint = startPosition + (rayDirection * m_MaxDistance);

            // Double check hit from gun muzzle to prevent near scenery weirdness
            if (useCamera)
            {
                Vector3 newRayDirection = hitPoint - muzzlePosition;
                newRayDirection.Normalize();
                ray = new Ray(muzzlePosition, newRayDirection);
                didHit = PhysicsExtensions.RaycastNonAllocSingle(ray, out m_Hit, m_MaxDistance, m_Layers, ignoreRoot, queryTriggers);
                if (didHit)
                    hitPoint = m_Hit.point;
            }

            // Draw the tracer line out to max distance
            if (m_TracerPrototype != null)
                StartCoroutine(ShowTracer(hitPoint));

            if (didHit)
                effect.Hit(m_Hit, ray.direction, m_Hit.distance, float.PositiveInfinity, firearm as IDamageSource);
			
            base.Shoot (accuracy, effect);
		}
        
        Transform GetRootTransform()
        {
            var t = transform;
            while (t.parent != null)
                t = t.parent;
            return t;
        }

        IEnumerator ShowTracer(Vector3 hitPoint)
        {
            yield return m_WaitForEndOfFrame;
            var tracer = PoolManager.GetPooledObject<IPooledHitscanTrail>(m_TracerPrototype);
            tracer.Show(m_MuzzleTip.position, hitPoint, m_TracerSize, m_TracerDuration);
        }

        private static readonly NeoSerializationKey k_LayersKey = new NeoSerializationKey("layers");

        public override void WriteProperties(INeoSerializer writer, NeoSerializedGameObject nsgo, SaveMode saveMode)
        {
            base.WriteProperties(writer, nsgo, saveMode);
            writer.WriteValue(k_LayersKey, m_Layers);
        }

        public override void ReadProperties(INeoDeserializer reader, NeoSerializedGameObject nsgo)
        {
            base.ReadProperties(reader, nsgo);
            int layers = m_Layers;
            if (reader.TryReadValue(k_LayersKey, out layers, layers))
                collisionLayers = layers;

        }
    }
}