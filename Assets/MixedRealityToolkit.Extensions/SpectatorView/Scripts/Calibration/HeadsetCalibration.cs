﻿using System.Collections.Generic;
using UnityEngine;

using Microsoft.MixedReality.Toolkit.Extensions.Experimental.MarkerDetection;
using Microsoft.MixedReality.Toolkit.Extensions.Experimental.SpectatorView.MarkerDetection;
using Microsoft.MixedReality.Toolkit.Extensions.Experimental.SpectatorView.Utilities;
using Microsoft.MixedReality.Toolkit.Extensions.PhotoCapture;
using System.Text;
using System;
using System.Collections.Concurrent;

namespace Microsoft.MixedReality.Toolkit.Extensions.Experimental.SpectatorView
{
    public delegate void HeadsetCalibrationDataUpdatedHandler(byte[] data);

    public class HeadsetCalibration : MonoBehaviour
    {
        /// <summary>
        /// Check to show debug visuals for the detected markers.
        /// </summary>
        [Tooltip("Check to show debug visuals for the detected markers.")]
        [SerializeField]
        protected bool showDebugVisuals = true;

        /// <summary>
        /// QR Code Marker Detector in scene
        /// </summary>
        [Tooltip("QR Code Marker Detector in scene")]
        [SerializeField]
        protected QRCodeMarkerDetector qrCodeMarkerDetector;

        /// <summary>
        /// Debug Visual Helper in scene that will place game objects on qr code markers in the scene.
        /// </summary>
        [Tooltip("Debug Visual Helper in scene that will place game objects on qr code markers in the scene.")]
        [SerializeField]
        protected DebugVisualHelper qrCodeDebugVisualHelper;

        /// <summary>
        /// Debug Visual Helper in scene that will place game objects on aruco markers in the scene.
        /// </summary>
        [Tooltip("Debug Visual Helper in scene that will place game objects on aruco markers in the scene.")]
        [SerializeField]
        protected DebugVisualHelper arucoDebugVisualHelper;

        public bool sendPVImage = false;

        protected HoloLensCamera holoLensCamera;
        private bool cameraSetup = false;
        private bool markersUpdated = false;
        private Dictionary<int, Marker> qrCodeMarkers = new Dictionary<int, Marker>();
        private Dictionary<int, GameObject> qrCodeDebugVisuals = new Dictionary<int, GameObject>();
        private Dictionary<int, GameObject> arucoDebugVisuals = new Dictionary<int, GameObject>();
        private readonly float markerPaddingRatio = 34f / (300f - (2f * 34f)); // padding pixels / marker width in pixels
        private Dictionary<int, MarkerCorners> qrCodeMarkerCorners = new Dictionary<int, MarkerCorners>();
        private Dictionary<int, MarkerCorners> arucoMarkerCorners = new Dictionary<int, MarkerCorners>();
        private ConcurrentQueue<HeadsetCalibrationData> dataQueue;
        private ConcurrentQueue<HeadsetCalibrationData> sendQueue;

        public event HeadsetCalibrationDataUpdatedHandler Updated;
        public void UpdateHeadsetCalibrationData()
        {
            if (sendPVImage &&
                !cameraSetup)
            {
                SetupCamera();
                cameraSetup = true;
            }

            if (dataQueue == null)
            {
                dataQueue = new ConcurrentQueue<HeadsetCalibrationData>();
            }

            Debug.Log("Updating headset calibration data");
            var data = new HeadsetCalibrationData();
            data.timestamp = Time.time;
            data.headsetData.position = Camera.main.transform.position;
            data.headsetData.rotation = Camera.main.transform.rotation;
            data.markers = new List<MarkerPair>();
            foreach (var qrCodePair in qrCodeMarkers)
            {
                if (qrCodeMarkerCorners.ContainsKey(qrCodePair.Key) &&
                    arucoMarkerCorners.ContainsKey(qrCodePair.Key))
                {
                    var markerPair = new MarkerPair();
                    markerPair.id = qrCodePair.Key;
                    markerPair.qrCodeMarkerCorners = qrCodeMarkerCorners[qrCodePair.Key];
                    markerPair.arucoMarkerCorners = arucoMarkerCorners[qrCodePair.Key];
                    data.markers.Add(markerPair);
                }
            }
            data.imageData = null;

            if (!sendPVImage)
            {
                sendQueue.Enqueue(data);
            }
            else
            {
                data.imageData = new PVImageData();
                dataQueue.Enqueue(data);

                Debug.Log("Taking photo with HoloLens PV Camera");
                if (!holoLensCamera.TakeSingle())
                {
                    Debug.Log("Failed to take photo, still setting up hololens camera");
                }
            }
        }

        private void Start()
        {
            sendQueue = new ConcurrentQueue<HeadsetCalibrationData>();

            qrCodeMarkerDetector.MarkersUpdated += OnQRCodesMarkersUpdated;
            qrCodeMarkerDetector.StartDetecting();
        }

        private void Update()
        {
            if (markersUpdated)
            {
                markersUpdated = false;
                ProcessQRCodeUpdate();
            }

            while (sendQueue.Count > 0)
            {
                if (sendQueue.TryDequeue(out var data))
                {
                    if (data.imageData != null &&
                        data.imageData.frame != null)
                    {
                        data.imageData.pngData = EncodeBGRAAsPNG(data.imageData.frame);
                        data.imageData.frame.Release();
                        data.imageData.frame = null;
                    }

                    SendHeadsetCalibrationDataPayload(data);
                }
            }
        }

        private void OnDestroy()
        {
            CleanUpCamera();
        }

        private void OnQRCodesMarkersUpdated(Dictionary<int, Marker> markers)
        {
            MergeDictionaries(qrCodeMarkers, markers);
            markersUpdated = true;
        }

        private void ProcessQRCodeUpdate()
        {
            HashSet<int> updatedMarkerIds = new HashSet<int>();

            foreach (var marker in qrCodeMarkers)
            {
                updatedMarkerIds.Add(marker.Key);
                float size = 0;
                if (qrCodeMarkerDetector.TryGetMarkerSize(marker.Key, out size))
                {
                    var qrCodePosition = marker.Value.Position;
                    var qrCodeRotation = marker.Value.Rotation;

                    if (showDebugVisuals)
                    {
                        GameObject qrCodeDebugVisual = null;
                        qrCodeDebugVisuals.TryGetValue(marker.Key, out qrCodeDebugVisual);
                        qrCodeDebugVisualHelper.CreateOrUpdateVisual(ref qrCodeDebugVisual, qrCodePosition, qrCodeRotation, size * Vector3.one);
                        qrCodeDebugVisuals[marker.Key] = qrCodeDebugVisual;
                    }

                    lock (qrCodeMarkerCorners)
                    {
                        qrCodeMarkerCorners[marker.Key] = CalculateMarkerCorners(qrCodePosition, qrCodeRotation, size);
                    }

                    var originToQRCode = Matrix4x4.TRS(qrCodePosition, qrCodeRotation, Vector3.one);
                    var arucoPosition = originToQRCode.MultiplyPoint(new Vector3(-1.0f * ((2.0f * (size * markerPaddingRatio)) + (size)), 0, 0));
                    // Assuming that the aruco marker has the same orientation as qr code marker.
                    // Because both the aruco marker and qr code marker are on the same plane/2d calibration board.
                    var arucoRotation = marker.Value.Rotation;

                    if (showDebugVisuals)
                    {
                        GameObject arucoDebugVisual = null;
                        arucoDebugVisuals.TryGetValue(marker.Key, out arucoDebugVisual);
                        arucoDebugVisualHelper.CreateOrUpdateVisual(ref arucoDebugVisual, arucoPosition, arucoRotation, size * Vector3.one);
                        arucoDebugVisuals[marker.Key] = arucoDebugVisual;
                    }

                    lock (arucoMarkerCorners)
                    {
                        arucoMarkerCorners[marker.Key] = CalculateMarkerCorners(arucoPosition, arucoRotation, size);
                    }
                }
            }

            RemoveItemsAndDestroy(qrCodeDebugVisuals, updatedMarkerIds);
            RemoveItemsAndDestroy(arucoDebugVisuals, updatedMarkerIds);
        }

        private async void SetupCamera()
        {
            Debug.Log("Setting up HoloLensCamera");
            if (holoLensCamera == null)
                holoLensCamera = new HoloLensCamera(CaptureMode.SingleLowLatency, PixelFormat.BGRA8);

            holoLensCamera.OnCameraInitialized += CameraInitialized;
            holoLensCamera.OnCameraStarted += CameraStarted;
            holoLensCamera.OnFrameCaptured += FrameCaptured;

            await holoLensCamera.Initialize();
        }

        private void CleanUpCamera()
        {
            Debug.Log("Cleaning up HoloLensCamera");
            if (holoLensCamera != null)
            {
                holoLensCamera.Dispose();
                holoLensCamera.OnCameraInitialized -= CameraInitialized;
                holoLensCamera.OnCameraStarted -= CameraStarted;
                holoLensCamera.OnFrameCaptured -= FrameCaptured;
                holoLensCamera = null;
            }
        }

        private void CameraInitialized(HoloLensCamera sender, bool initializeSuccessful)
        {
            if (!initializeSuccessful)
            {
                Debug.Log("Camera failed to initialize");
            }

            Debug.Log("HoloLensCamera initialized");
            var descriptions = sender.StreamSelector.Select(StreamCompare.EqualTo, 1408, 792).StreamDescriptions;
            if (descriptions.Count > 0)
            {
                StreamDescription streamDesc = descriptions[0];
                sender.Start(streamDesc);
            }
            else
            {
                Debug.LogWarning("Expected camera resolution not supported");
            }
        }

        private void CameraStarted(HoloLensCamera sender, bool startSuccessful)
        {
            if (startSuccessful)
            {
                Debug.Log("HoloLensCamera successfully started");
            }
            else
            {
                Debug.LogError("Error: HoloLensCamera failed to start");
            }
        }

        private void FrameCaptured(HoloLensCamera sender, CameraFrame frame)
        {
#if UNITY_WSA
            if (dataQueue == null ||
                dataQueue.Count == 0)
            {
                Debug.Log("Data queue didn't contain any content, frame dropped");
                return;
            }

            Debug.Log("Image obtained from HoloLens PV Camera");

            // Always obtain the most recent request. Some requests may be dropped, but we should use the most recent headpose
            HeadsetCalibrationData data = null;
            while (!dataQueue.IsEmpty)
            {
                dataQueue.TryDequeue(out data);
            }

            if (data != null)
            {
                data.imageData.pixelFormat = frame.PixelFormat;
                data.imageData.resolution = frame.Resolution;
                data.imageData.intrinsics = frame.Intrinsics;
                data.imageData.extrinsics = frame.Extrinsics;
                frame.AddRef();
                data.imageData.frame = frame;

                Debug.Log($"Frame obtained with resolution {frame.Resolution.Width} x {frame.Resolution.Height} and size {frame.PixelData.Length}");
                sendQueue.Enqueue(data);
            }
            else
            {
                Debug.Log("Image was captured but no headset data existed for payloads to send");
            }
#endif
        }

        private void SendHeadsetCalibrationDataPayload(HeadsetCalibrationData data)
        {
            byte[] payload = null;
            payload = Encoding.ASCII.GetBytes(JsonUtility.ToJson(data));

            Updated?.Invoke(payload);
        }

        private static void MergeDictionaries(Dictionary<int, Marker> dictionary, Dictionary<int, Marker> update)
        {
            HashSet<int> observedMarkers = new HashSet<int>();
            foreach(var markerUpdate in update)
            {
                dictionary[markerUpdate.Key] = markerUpdate.Value;
                observedMarkers.Add(markerUpdate.Key);
            }

            RemoveItems(dictionary, observedMarkers);
        }

        private static void RemoveItems<TKey, TValue>(Dictionary<TKey, TValue> items, HashSet<TKey> itemsToKeep)
        {
            List<TKey> keysToRemove = new List<TKey>();
            foreach (var pair in items)
            {
                if (!itemsToKeep.Contains(pair.Key))
                {
                    keysToRemove.Add(pair.Key);
                }
            }

            foreach (var key in keysToRemove)
            {
                items.Remove(key);
            }
        }

        private static void RemoveItemsAndDestroy<TKey>(Dictionary<TKey, GameObject> items, HashSet<TKey> itemsToKeep)
        {
            List<TKey> keysToRemove = new List<TKey>();
            foreach (var pair in items)
            {
                if (!itemsToKeep.Contains(pair.Key))
                {
                    keysToRemove.Add(pair.Key);
                }
            }

            foreach (var key in keysToRemove)
            {
                Debug.Log($"Destroying debug visual for marker id:{key}");
                var visual = items[key];
                items.Remove(key);
                Destroy(visual);
            }
        }

        private static Vector4 GetPosition(Matrix4x4 matrix)
        {
            return matrix.GetColumn(3);
        }

        private static Quaternion GetRotation(Matrix4x4 matrix)
        {
            return Quaternion.LookRotation(matrix.GetColumn(2), matrix.GetColumn(1));
        }

        private static MarkerCorners CalculateMarkerCorners(Vector3 topLeftPosition, Quaternion topLeftOrientation, float size)
        {
            var corners = new MarkerCorners();
            corners.topLeft = topLeftPosition;
            var originToTopLeftCorner = Matrix4x4.TRS(topLeftPosition, topLeftOrientation, Vector3.one);
            corners.topRight = originToTopLeftCorner.MultiplyPoint(new Vector3(-size, 0, 0));
            corners.bottomLeft = originToTopLeftCorner.MultiplyPoint(new Vector3(0, -size, 0));
            corners.bottomRight = originToTopLeftCorner.MultiplyPoint(new Vector3(-size, -size, 0));
            corners.orientation = topLeftOrientation;
            return corners;
        }

        private static byte[] EncodeBGRAAsPNG(CameraFrame frame)
        {
            Texture2D texture = new Texture2D((int)frame.Resolution.Width, (int)frame.Resolution.Height, TextureFormat.BGRA32, false);

            // Byte data obtained from a HoloLensCamera needs to be flipped vertically to display correctly in Unity
            byte[] copy = new byte[frame.PixelData.Length];
            int stride = (int)frame.Resolution.Width * 4;
            for (int i = 0; i < frame.Resolution.Height; i++)
            {
                Array.Copy(frame.PixelData, (int)(frame.Resolution.Height - i - 1) * stride, copy, i * stride, stride);
            }

            texture.LoadRawTextureData(copy);
            texture.Apply();
            return texture.EncodeToPNG();
        }
    }
}
