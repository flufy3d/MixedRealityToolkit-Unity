﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.MixedReality.Toolkit.Utilities;
using System;
using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;

namespace Microsoft.MixedReality.Toolkit.Input
{
    /// <summary>
    /// Hand visualizer that controls a hierarchy of transforms to be used by a SkinnedMeshRenderer
    /// Implementation is derived from LeapMotion RiggedHand and RiggedFinger and has visual parity
    /// </summary>
    public class RiggedHandVisualizer : BaseHandVisualizer, IMixedRealityHandJointHandler
    {
        // This bool is used to track whether or not we are recieving hand mesh data from the platform itself
        // If we aren't we will use our own rigged hand visualizer to render the hand mesh
        private bool receivingPlatformHandMesh;

        /// <summary>
        /// Wrist Transform
        /// </summary>
        public Transform Wrist;
        /// <summary>
        /// Palm transform
        /// </summary>
        public Transform Palm;
        /// <summary>
        /// Thumb metacarpal transform  (thumb root)
        /// </summary>
        public Transform ThumbRoot;

        [Tooltip("First finger node is metacarpal joint.")]
        public bool ThumbRootIsMetacarpal = true;

        /// <summary>
        /// Index metacarpal transform (index finger root)
        /// </summary>
        public Transform IndexRoot;

        [Tooltip("First finger node is metacarpal joint.")]
        public bool IndexRootIsMetacarpal = true;

        /// <summary>
        /// Middle metacarpal transform (middle finger root)
        /// </summary>
        public Transform MiddleRoot;

        [Tooltip("First finger node is metacarpal joint.")]
        public bool MiddleRootIsMetacarpal = true;

        /// <summary>
        /// Ring metacarpal transform (ring finger root)
        /// </summary>
        public Transform RingRoot;

        [Tooltip("Ring finger node is metacarpal joint.")]
        public bool RingRootIsMetacarpal = true;

        /// <summary>
        /// Pinky metacarpal transform (pinky finger root)
        /// </summary>
        public Transform PinkyRoot;

        [Tooltip("First finger node is metacarpal joint.")]
        public bool PinkyRootIsMetacarpal = true;

        [Tooltip("Hands are typically rigged in 3D packages with the palm transform near the wrist. Uncheck this if your model's palm transform is at the center of the palm similar to Leap API hands.")]
        public bool ModelPalmAtLeapWrist = true;

        [Tooltip("Allows the mesh to be stretched to align with finger joint positions. Only set to true when mesh is not visible.")]
        public bool DeformPosition = true;

        [Tooltip("Because bones only exist at their roots in model rigs, the length " +
          "of the last fingertip bone is lost when placing bones at positions in the " +
          "tracked hand. " +
          "This option scales the last bone along its X axis (length axis) to match " +
          "its bone length to the tracked bone length. This option only has an " +
          "effect if Deform Positions In Fingers is enabled.")]
        public bool ScaleLastFingerBone = true;

        [Tooltip("If non-zero, this vector and the modelPalmFacing vector " +
        "will be used to re-orient the Transform bones in the hand rig, to " +
        "compensate for bone axis discrepancies between Leap Bones and model " +
        "bones.")]
        public Vector3 ModelFingerPointing = new Vector3(0, 0, 0);

        [Tooltip("If non-zero, this vector and the modelFingerPointing vector " +
          "will be used to re-orient the Transform bones in the hand rig, to " +
          "compensate for bone axis discrepancies between Leap Bones and model " +
          "bones.")]
        public Vector3 ModelPalmFacing = new Vector3(0, 0, 0);

        [SerializeField]
        [Tooltip("Renderer of the hand mesh")]
        private SkinnedMeshRenderer handRenderer = null;

        /// <summary>
        /// flag checking that the handRenderer was initialized with its own material
        /// </summary>
        private bool handRendererInitialized = false;

        /// <summary>
        /// Renderer of the hand mesh.
        /// </summary>
        public SkinnedMeshRenderer HandRenderer => handRenderer;

        [SerializeField]
        [Tooltip("Hand material to use for hand tracking hand mesh.")]
        private Material handMaterial = null;

        /// <summary>
        /// Hand material to use for hand tracking hand mesh.
        /// </summary>
        public Material HandMaterial => handMaterial;

        /// <summary>
        /// Property name for modifying the mesh's appearance based on pinch strength
        /// </summary>
        private const string pinchStrengthMaterialProperty = "_PressIntensity";

        /// <summary>
        /// Property name for modifying the mesh's appearance based on pinch strength
        /// </summary>
        public string PinchStrengthMaterialProperty => pinchStrengthMaterialProperty;

        /// <summary>
        /// Precalculated values for LeapMotion testhand fingertip lengths
        /// </summary>
        private const float thumbFingerTipLength = 0.02167f;
        private const float indexingerTipLength = 0.01582f;
        private const float middleFingerTipLength = 0.0174f;
        private const float ringFingerTipLength = 0.0173f;
        private const float pinkyFingerTipLength = 0.01596f;

        /// <summary>
        /// Precalculated fingertip lengths used for scaling the fingertips of the skinnedmesh
        /// to match with tracked hand fingertip size 
        /// </summary>
        private Dictionary<TrackedHandJoint, float> fingerTipLengths = new Dictionary<TrackedHandJoint, float>()
        {
            {TrackedHandJoint.ThumbTip, thumbFingerTipLength },
            {TrackedHandJoint.IndexTip, indexingerTipLength },
            {TrackedHandJoint.MiddleTip, middleFingerTipLength },
            {TrackedHandJoint.RingTip, ringFingerTipLength },
            {TrackedHandJoint.PinkyTip, pinkyFingerTipLength }
        };

        /// <summary>
        /// Rotation derived from the `modelFingerPointing` and
        /// `modelPalmFacing` vectors in the RiggedHand inspector.
        /// </summary>
        private Quaternion UserBoneRotation
        {
            get
            {
                if (ModelFingerPointing == Vector3.zero || ModelPalmFacing == Vector3.zero)
                {
                    return Quaternion.identity;
                }
                return Quaternion.Inverse(Quaternion.LookRotation(ModelFingerPointing, -ModelPalmFacing));
            }
        }

        protected override void Start()
        {
            base.Start();

            // Ensure hand is not visible until we can update position first time.
            HandRenderer.enabled = false;

            // Initialize joint dictionary with their corresponding joint transforms
            jointsArray[(int)TrackedHandJoint.Wrist] = Wrist;
            jointsArray[(int)TrackedHandJoint.Palm] = Palm;

            // Thumb jointsArray, first node is user assigned, note that there are only 4 jointsArray in the thumb
            if (ThumbRoot)
            {
                if (ThumbRootIsMetacarpal)
                {
                    jointsArray[(int)TrackedHandJoint.ThumbMetacarpalJoint] = ThumbRoot;
                    jointsArray[(int)TrackedHandJoint.ThumbProximalJoint] = RetrieveChild(TrackedHandJoint.ThumbMetacarpalJoint);
                }
                else
                {
                    jointsArray[(int)TrackedHandJoint.ThumbProximalJoint] = ThumbRoot;
                }
                jointsArray[(int)TrackedHandJoint.ThumbDistalJoint] = RetrieveChild(TrackedHandJoint.ThumbProximalJoint);
                jointsArray[(int)TrackedHandJoint.ThumbTip] = RetrieveChild(TrackedHandJoint.ThumbDistalJoint);
            }
            // Look up index finger jointsArray below the index finger root joint
            if (IndexRoot)
            {
                if (IndexRootIsMetacarpal)
                {
                    jointsArray[(int)TrackedHandJoint.IndexMetacarpal] = IndexRoot;
                    jointsArray[(int)TrackedHandJoint.IndexKnuckle] = RetrieveChild(TrackedHandJoint.IndexMetacarpal);
                }
                else
                {
                    jointsArray[(int)TrackedHandJoint.IndexKnuckle] = IndexRoot;
                }
                jointsArray[(int)TrackedHandJoint.IndexMiddleJoint] = RetrieveChild(TrackedHandJoint.IndexKnuckle);
                jointsArray[(int)TrackedHandJoint.IndexDistalJoint] = RetrieveChild(TrackedHandJoint.IndexMiddleJoint);
                jointsArray[(int)TrackedHandJoint.IndexTip] = RetrieveChild(TrackedHandJoint.IndexDistalJoint);
            }

            // Look up middle finger jointsArray below the middle finger root joint
            if (MiddleRoot)
            {
                if (MiddleRootIsMetacarpal)
                {
                    jointsArray[(int)TrackedHandJoint.MiddleMetacarpal] = MiddleRoot;
                    jointsArray[(int)TrackedHandJoint.MiddleKnuckle] = RetrieveChild(TrackedHandJoint.MiddleMetacarpal);
                }
                else
                {
                    jointsArray[(int)TrackedHandJoint.MiddleKnuckle] = MiddleRoot;
                }
                jointsArray[(int)TrackedHandJoint.MiddleMiddleJoint] = RetrieveChild(TrackedHandJoint.MiddleKnuckle);
                jointsArray[(int)TrackedHandJoint.MiddleDistalJoint] = RetrieveChild(TrackedHandJoint.MiddleMiddleJoint);
                jointsArray[(int)TrackedHandJoint.MiddleTip] = RetrieveChild(TrackedHandJoint.MiddleDistalJoint);
            }

            // Look up ring finger jointsArray below the ring finger root joint
            if (RingRoot)
            {
                if (RingRootIsMetacarpal)
                {
                    jointsArray[(int)TrackedHandJoint.RingMetacarpal] = RingRoot;
                    jointsArray[(int)TrackedHandJoint.RingKnuckle] = RetrieveChild(TrackedHandJoint.RingMetacarpal);
                }
                else
                {
                    jointsArray[(int)TrackedHandJoint.RingKnuckle] = RingRoot;
                }
                jointsArray[(int)TrackedHandJoint.RingMiddleJoint] = RetrieveChild(TrackedHandJoint.RingKnuckle);
                jointsArray[(int)TrackedHandJoint.RingDistalJoint] = RetrieveChild(TrackedHandJoint.RingMiddleJoint);
                jointsArray[(int)TrackedHandJoint.RingTip] = RetrieveChild(TrackedHandJoint.RingDistalJoint);
            }

            // Look up pinky jointsArray below the pinky root joint
            if (PinkyRoot)
            {
                if (PinkyRootIsMetacarpal)
                {
                    jointsArray[(int)TrackedHandJoint.PinkyMetacarpal] = PinkyRoot;
                    jointsArray[(int)TrackedHandJoint.PinkyKnuckle] = RetrieveChild(TrackedHandJoint.PinkyMetacarpal);
                }
                else
                {
                    jointsArray[(int)TrackedHandJoint.PinkyKnuckle] = PinkyRoot;
                }
                jointsArray[(int)TrackedHandJoint.PinkyMiddleJoint] = RetrieveChild(TrackedHandJoint.PinkyKnuckle);
                jointsArray[(int)TrackedHandJoint.PinkyDistalJoint] = RetrieveChild(TrackedHandJoint.PinkyMiddleJoint);
                jointsArray[(int)TrackedHandJoint.PinkyTip] = RetrieveChild(TrackedHandJoint.PinkyDistalJoint);
            }

            // Give the hand mesh its own material to avoid modifying both hand materials when making property changes
            var handMaterialInstance = new Material(handMaterial);
            handRenderer.sharedMaterial = handMaterialInstance;
            handRendererInitialized = true;
        }

        private Transform RetrieveChild(TrackedHandJoint parentJoint)
        {
            Transform parentJointTransform = jointsArray[(int)parentJoint];
            if (parentJointTransform != null && parentJointTransform.childCount > 0)
            {
                return parentJointTransform.GetChild(0);
            }
            return null;
        }

        private void OnEnable()
        {
            CoreServices.InputSystem?.RegisterHandler<IMixedRealitySourceStateHandler>(this);
            CoreServices.InputSystem?.RegisterHandler<IMixedRealityHandJointHandler>(this);
        }

        private void OnDisable()
        {
            CoreServices.InputSystem?.UnregisterHandler<IMixedRealitySourceStateHandler>(this);
            CoreServices.InputSystem?.UnregisterHandler<IMixedRealityHandJointHandler>(this);
        }

        private bool displayedMaterialPropertyWarning = false;


        private static readonly ProfilerMarker OnHandJointsUpdatedPerfMarker = new ProfilerMarker("[MRTK] RiggedHandVisualizer.OnHandJointsUpdated");

        /// <inheritdoc/>
        public override void OnHandJointsUpdated(InputEventData<IDictionary<TrackedHandJoint, MixedRealityPose>> eventData)
        {
            using (OnHandJointsUpdatedPerfMarker.Auto())
            {
                // The base class takes care of updating all of the joint data
                base.OnHandJointsUpdated(eventData);

                IMixedRealityInputSystem inputSystem = CoreServices.InputSystem;
                MixedRealityHandTrackingProfile handTrackingProfile = inputSystem?.InputSystemProfile.HandTrackingProfile;

                if (receivingPlatformHandMesh)
                {
                    // exit early if we've gotten a hand mesh from the underlying platform
                    return;
                }

                // Only runs if render hand mesh is true
                bool renderHandmesh = handTrackingProfile != null && handTrackingProfile.EnableHandMeshVisualization;
                HandRenderer.enabled = renderHandmesh;
                if (renderHandmesh)
                {
                    // Render the rigged hand mesh itself
                    // Apply updated TrackedHandJoint pose data to the assigned transforms

                    // This starts at 1 to skip over TrackedHandJoint.None.
                    for (int i = 1; i < ArticulatedHandPose.JointCount; i++)
                    {
                        TrackedHandJoint handJoint = (TrackedHandJoint)i;
                        MixedRealityPose handJointPose = eventData.InputData[handJoint];
                        Transform jointTransform = jointsArray[i];

                        if (jointTransform != null)
                        {
                            if (handJoint == TrackedHandJoint.Palm)
                            {
                                if (ModelPalmAtLeapWrist)
                                {
                                    Palm.position = eventData.InputData[TrackedHandJoint.Wrist].Position;
                                }
                                else
                                {
                                    Palm.position = handJointPose.Position;
                                }
                                Palm.rotation = handJointPose.Rotation * UserBoneRotation;
                            }
                            else if (handJoint == TrackedHandJoint.Wrist)
                            {
                                if (!ModelPalmAtLeapWrist)
                                {
                                    Wrist.position = handJointPose.Position;
                                }
                            }
                            else
                            {
                                // Finger jointsArray
                                jointTransform.rotation = handJointPose.Rotation * Reorientation();

                                if (DeformPosition)
                                {
                                    jointTransform.position = handJointPose.Position;
                                }

                                if (ScaleLastFingerBone &&
                                    (handJoint == TrackedHandJoint.ThumbDistalJoint ||
                                    handJoint == TrackedHandJoint.IndexDistalJoint ||
                                    handJoint == TrackedHandJoint.MiddleDistalJoint ||
                                    handJoint == TrackedHandJoint.RingDistalJoint ||
                                    handJoint == TrackedHandJoint.PinkyDistalJoint))
                                {
                                    ScaleFingerTip(eventData, jointTransform, handJoint + 1, jointTransform.position);
                                }
                            }
                        }
                    }

                    // Update the hand material
                    float pinchStrength = HandPoseUtils.CalculateIndexPinch(Controller.ControllerHandedness);

                    // Hand Curl Properties: 
                    float indexFingerCurl = HandPoseUtils.IndexFingerCurl(Controller.ControllerHandedness);
                    float middleFingerCurl = HandPoseUtils.MiddleFingerCurl(Controller.ControllerHandedness);
                    float ringFingerCurl = HandPoseUtils.RingFingerCurl(Controller.ControllerHandedness);
                    float pinkyFingerCurl = HandPoseUtils.PinkyFingerCurl(Controller.ControllerHandedness);

                    if (handMaterial != null && handRendererInitialized)
                    {
                        float gripStrength = indexFingerCurl + middleFingerCurl + ringFingerCurl + pinkyFingerCurl;
                        gripStrength /= 4.0f;
                        gripStrength = gripStrength > 0.8f ? 1.0f : gripStrength;

                        pinchStrength = Mathf.Pow(Mathf.Max(pinchStrength, gripStrength), 2.0f);

                        if (handRenderer.sharedMaterial.HasProperty(pinchStrengthMaterialProperty))
                        {
                            handRenderer.sharedMaterial.SetFloat(pinchStrengthMaterialProperty, pinchStrength);
                        }
                        // Only show this warning once
                        else if (!displayedMaterialPropertyWarning)
                        {
                            Debug.LogWarning(String.Format("The property {0} for reacting to pinch strength was not found. A material with this property is required to visualize pinch strength.", pinchStrengthMaterialProperty));
                            displayedMaterialPropertyWarning = true;
                        }
                    }
                }
            }
        }

        /// <inheritdoc/>
        public override void OnHandMeshUpdated(InputEventData<HandMeshInfo> eventData)
        {
            // We've gotten hand mesh data from the platform, don't use the rigged hand models for visualization
            if (eventData.Handedness != Controller?.ControllerHandedness)
            {
                receivingPlatformHandMesh = true;
            }

            base.OnHandMeshUpdated(eventData);
        }

        private Quaternion Reorientation()
        {
            return Quaternion.Inverse(Quaternion.LookRotation(ModelFingerPointing, -ModelPalmFacing));
        }

        private void ScaleFingerTip(InputEventData<IDictionary<TrackedHandJoint, MixedRealityPose>> eventData, Transform jointTransform, TrackedHandJoint fingerTipJoint, Vector3 boneRootPos)
        {
            // Set fingertip base bone scale to match the bone length to the fingertip.
            // This will only scale correctly if the model was constructed to match
            // the standard "test" edit-time hand model from the LeapMotion TestHandFactory.
            var boneTipPos = eventData.InputData[fingerTipJoint].Position;
            var boneVec = boneTipPos - boneRootPos;

            if (transform.lossyScale.x != 0f && transform.lossyScale.x != 1f)
            {
                boneVec /= transform.lossyScale.x;
            }
            var newScale = jointTransform.transform.localScale;
            var lengthComponentIdx = GetLargestComponentIndex(ModelFingerPointing);
            newScale[lengthComponentIdx] = boneVec.magnitude / fingerTipLengths[fingerTipJoint];
            jointTransform.transform.localScale = newScale;
        }

        private int GetLargestComponentIndex(Vector3 pointingVector)
        {
            var largestValue = 0f;
            var largestIdx = 0;
            for (int i = 0; i < 3; i++)
            {
                var testValue = pointingVector[i];
                if (Mathf.Abs(testValue) > largestValue)
                {
                    largestIdx = i;
                    largestValue = Mathf.Abs(testValue);
                }
            }
            return largestIdx;
        }
    }
}
