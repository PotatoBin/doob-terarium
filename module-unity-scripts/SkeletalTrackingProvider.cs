using Microsoft.Azure.Kinect.BodyTracking;
using Microsoft.Azure.Kinect.Sensor;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

public class SkeletalTrackingProvider : BackgroundDataProvider
{
    bool readFirstFrame = false;
    TimeSpan initialTimestamp;

    public SkeletalTrackingProvider(int id) : base(id)
    {
        Debug.Log("in the skeleton provider constructor");
    }

    System.Runtime.Serialization.Formatters.Binary.BinaryFormatter binaryFormatter { get; set; } = new System.Runtime.Serialization.Formatters.Binary.BinaryFormatter();

    public Stream RawDataLoggingFile = null;

    protected override void RunBackgroundThreadAsync(int id, CancellationToken token)
    {
        try
        {
            UnityEngine.Debug.Log("Starting body tracker background thread.");

            // Buffer allocations
            BackgroundData currentFrameData = new BackgroundData();

            // Open device
            using (Device device = Device.Open(id))
            {
                // 🔥 중요: synchronized_images_only = true 추가!
                device.StartCameras(new DeviceConfiguration()
                {
                    CameraFPS = FPS.FPS30,
                    ColorResolution = ColorResolution.R720p, // 1280x720 RGB
                    ColorFormat = ImageFormat.ColorBGRA32,   // 🆕 명시적 포맷 지정
                    DepthMode = DepthMode.NFOV_Unbinned,
                    WiredSyncMode = WiredSyncMode.Standalone,
                    SynchronizedImagesOnly = true // 🔥 이게 핵심!
                });

                UnityEngine.Debug.Log("Open K4A device successful. id " + id + " sn:" + device.SerialNum);
                UnityEngine.Debug.Log("🔥 RGB-Depth synchronization ENABLED");

                var deviceCalibration = device.GetCalibration();

                using (Tracker tracker = Tracker.Create(deviceCalibration, new TrackerConfiguration()
                {
                    ProcessingMode = TrackerProcessingMode.Cuda,
                    SensorOrientation = SensorOrientation.Default
                }))
                {
                    UnityEngine.Debug.Log("Body tracker created.");

                    while (!token.IsCancellationRequested)
                    {
                        // 🔥 synchronized_images_only = true이므로
                        // Depth와 Color가 동시에 캡처됨
                        using (Capture sensorCapture = device.GetCapture())
                        {
                            // Queue latest frame from the sensor
                            tracker.EnqueueCapture(sensorCapture);
                        }

                        // Try getting latest tracker frame
                        using (Frame frame = tracker.PopResult(TimeSpan.Zero, throwOnTimeout: false))
                        {
                            if (frame == null)
                            {
                                // 정상적인 상황 - 프레임이 아직 준비 안됨
                                continue;
                            }

                            IsRunning = true;

                            // Get number of bodies in the current frame
                            currentFrameData.NumOfBodies = frame.NumberOfBodies;

                            // Copy bodies (최대 6명까지만)
                            uint bodiesToCopy = (uint)System.Math.Min((ulong)currentFrameData.NumOfBodies, 6UL);
                            for (uint i = 0; i < bodiesToCopy; i++)
                            {
                                try
                                {
                                    currentFrameData.Bodies[i].CopyFromBodyTrackingSdk(frame.GetBody(i), deviceCalibration);
                                }
                                catch (System.Exception e)
                                {
                                    UnityEngine.Debug.LogWarning($"Failed to copy body {i}: {e.Message}");
                                }
                            }

                            Capture bodyFrameCapture = frame.Capture;

                            // ===== Depth Image =====
                            Image depthImage = bodyFrameCapture.Depth;
                            if (depthImage == null)
                            {
                                UnityEngine.Debug.LogWarning("Depth image is null!");
                                continue;
                            }

                            if (!readFirstFrame)
                            {
                                readFirstFrame = true;
                                initialTimestamp = depthImage.DeviceTimestamp;
                            }
                            currentFrameData.TimestampInMs = (float)(depthImage.DeviceTimestamp - initialTimestamp).TotalMilliseconds;
                            currentFrameData.DepthImageWidth = depthImage.WidthPixels;
                            currentFrameData.DepthImageHeight = depthImage.HeightPixels;

                            var depthFrame = MemoryMarshal.Cast<byte, ushort>(depthImage.Memory.Span);
                            int byteCounter = 0;
                            currentFrameData.DepthImageSize = currentFrameData.DepthImageWidth * currentFrameData.DepthImageHeight * 3;

                            for (int it = currentFrameData.DepthImageWidth * currentFrameData.DepthImageHeight - 1; it > 0; it--)
                            {
                                byte b = (byte)(depthFrame[it] / (ConfigLoader.Instance.Configs.SkeletalTracking.MaximumDisplayedDepthInMillimeters) * 255);
                                currentFrameData.DepthImage[byteCounter++] = b;
                                currentFrameData.DepthImage[byteCounter++] = b;
                                currentFrameData.DepthImage[byteCounter++] = b;
                            }

                            // ===== 🔥 RGB Image (동기화됨) =====
                            Image colorImage = bodyFrameCapture.Color;

                            if (colorImage != null)
                            {
                                currentFrameData.ColorImageWidth = colorImage.WidthPixels;
                                currentFrameData.ColorImageHeight = colorImage.HeightPixels;

                                // BGRA32 포맷 (4 bytes per pixel)
                                var colorSpan = colorImage.Memory.Span;
                                int colorSize = currentFrameData.ColorImageWidth * currentFrameData.ColorImageHeight * 4;
                                currentFrameData.ColorImageSize = colorSize;

                                // 안전한 복사
                                int bytesToCopy = System.Math.Min(colorSize, currentFrameData.ColorImage.Length);
                                bytesToCopy = System.Math.Min(bytesToCopy, colorSpan.Length);

                                if (bytesToCopy > 0)
                                {
                                    colorSpan.Slice(0, bytesToCopy).CopyTo(currentFrameData.ColorImage.AsSpan(0, bytesToCopy));
                                }

                                // 첫 프레임만 로그 출력
                                if (!readFirstFrame)
                                {
                                    UnityEngine.Debug.Log($"✅ Color image captured: {currentFrameData.ColorImageWidth}x{currentFrameData.ColorImageHeight}, {bytesToCopy} bytes");
                                }
                            }
                            else
                            {
                                // 🔥 동기화가 제대로 안되면 여기로 옴
                                currentFrameData.ColorImageSize = 0;
                                UnityEngine.Debug.LogWarning("⚠️ Color image is NULL despite synchronized_images_only = true!");
                            }

                            if (RawDataLoggingFile != null && RawDataLoggingFile.CanWrite)
                            {
                                binaryFormatter.Serialize(RawDataLoggingFile, currentFrameData);
                            }

                            // Update data variable that is being read in the UI thread
                            SetCurrentFrameData(ref currentFrameData);
                        }
                    }
                    Debug.Log("dispose of tracker now!!!!!");
                    tracker.Dispose();
                }
                device.Dispose();
            }
            if (RawDataLoggingFile != null)
            {
                RawDataLoggingFile.Close();
            }
        }
        catch (Exception e)
        {
            Debug.Log($"catching exception for background thread {e.Message}");
            token.ThrowIfCancellationRequested();
        }
    }
}