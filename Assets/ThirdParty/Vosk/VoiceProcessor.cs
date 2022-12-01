/* * * * *
 * A unity voice processor
 * ------------------------------
 * 
 * A Unity script for recording and delivering frames of audio for real-time processing
 * 
 * Written by Picovoice 
 * 2021-02-19
 * 
 * Apache License
 * 
 * Copyright (c) 2021 Picovoice
 * 
 * Licensed under the Apache License, Version 2.0 (the "License");
 *   you may not use this file except in compliance with the License.
 *   You may obtain a copy of the License at
 *   
 *   http://www.apache.org/licenses/LICENSE-2.0
 *   
 *   Unless required by applicable law or agreed to in writing, software
 *   distributed under the License is distributed on an "AS IS" BASIS,
 *   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 *   See the License for the specific language governing permissions and
 *   limitations under the License.
 * 
 * * * * */
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;


/// <summary>
/// Class that records audio and delivers frames for real-time audio processing
/// </summary>
public class VoiceProcessor : MonoBehaviour
{
    /// <summary>
    /// Indicates whether microphone is capturing or not
    /// </summary>
    public bool IsRecording => _audioClip != null && Microphone.IsRecording(CurrentDeviceName);

    /// <summary>
    /// The samples are floats ranging from -1.0f to 1.0f, representing the data in the audio clip
    /// </summary>
    static float[] samplesData;
    /// <summary>
    /// WAV file header size
    /// </summary>
    const int HEADER_SIZE = 44;
    
    [SerializeField] private int MicrophoneIndex;
    public int recordDuration = 60;
    
    /// <summary>
    /// Sample rate of recorded audio
    /// </summary>
    public int SampleRate { get; private set; }

    
    /// <summary>
    /// Size of audio frames that are delivered
    /// </summary>
    public int FrameLength { get; private set; }

    /// <summary>
    /// Event where frames of audio are delivered
    /// </summary>
    public event Action<short[]> OnFrameCaptured;

    /// <summary>
    /// Event when audio capture thread stops
    /// </summary>
    public event Action OnRecordingStop;

    /// <summary>
    /// Event when audio capture thread starts
    /// </summary>
    public event Action OnRecordingStart;

    /// <summary>
    /// Available audio recording devices
    /// </summary>
    public List<string> Devices { get; private set; }

    /// <summary>
    /// Index of selected audio recording device
    /// </summary>
    public int CurrentDeviceIndex { get; private set; }

    /// <summary>
    /// Name of selected audio recording device
    /// </summary>
    public string CurrentDeviceName
    {
        get
        {
            if (CurrentDeviceIndex < 0 || CurrentDeviceIndex >= Microphone.devices.Length)
            {
                return string.Empty;
            }
            return Devices[CurrentDeviceIndex];
        }
    }

    [Header("Voice Detection Settings")]
    [SerializeField, Tooltip("The minimum volume to detect voice input for"), Range(0.0f, 1.0f)]
    private float _minimumSpeakingSampleValue = 0.05f;

    [SerializeField, Tooltip("Time in seconds of detected silence before voice request is sent")]
    private float _silenceTimer = 1.0f;

    [SerializeField, Tooltip("Auto detect speech using the volume threshold.")]
    private bool _autoDetect;

    private float _timeAtSilenceBegan;
    private bool _audioDetected;
    private bool _didDetect;
    private bool _transmit;


    AudioClip _audioClip;
    public bool markStopRecording = false;
    private event Action RestartRecording;
    public event Action<string> OnRecordSaved;

    void Awake()
    {
        UpdateDevices();
    }

#if UNITY_EDITOR
    void Update()
    {
        if (CurrentDeviceIndex != MicrophoneIndex)
        {
            ChangeDevice(MicrophoneIndex);
        }
    }
#endif

    /// <summary>
    /// Updates list of available audio devices
    /// </summary>
    public void UpdateDevices()
    {
        Devices = new List<string>();
        foreach (var device in Microphone.devices)
            Devices.Add(device);

        if (Devices == null || Devices.Count == 0)
        {
            CurrentDeviceIndex = -1;
            Debug.LogError("There is no valid recording device connected");
            return;
        }

        CurrentDeviceIndex = MicrophoneIndex;
    }

    /// <summary>
    /// Change audio recording device
    /// </summary>
    /// <param name="deviceIndex">Index of the new audio capture device</param>
    public void ChangeDevice(int deviceIndex)
    {
        if (deviceIndex < 0 || deviceIndex >= Devices.Count)
        {
            Debug.LogError(string.Format("Specified device index {0} is not a valid recording device", deviceIndex));
            return;
        }

        if (IsRecording)
        {
            // one time event to restart recording with the new device 
            // the moment the last session has completed
            RestartRecording += () =>
            {
                CurrentDeviceIndex = deviceIndex;
                StartRecording(SampleRate, FrameLength);
                RestartRecording = null;
            };
            StopRecording();
        }
        else
        {
            CurrentDeviceIndex = deviceIndex;
        }
    }

    /// <summary>
    /// Start recording audio
    /// </summary>
    /// <param name="sampleRate">Sample rate to record at</param>
    /// <param name="frameSize">Size of audio frames to be delivered</param>
    /// <param name="autoDetect">Should the audio continuously record based on the volume</param>
    public void StartRecording(int sampleRate = 16000, int frameSize = 512, bool ?autoDetect = null)
    {
        markStopRecording = false;
        if (autoDetect != null)
        {
            _autoDetect = (bool) autoDetect;
        }

        if (IsRecording)
        {
            if (sampleRate != SampleRate || frameSize != FrameLength)
            {
                RestartRecording += () =>
                {
                    StartRecording(SampleRate, FrameLength, autoDetect);
                    RestartRecording = null;
                };
                StopRecording();
            }
            return;
        }

        SampleRate = sampleRate;
        FrameLength = frameSize;
        _audioClip = Microphone.Start(CurrentDeviceName, true, recordDuration, sampleRate);
        StartCoroutine(RecordData());
    }

    /// <summary>
    /// Stops recording audio
    /// </summary>
    public void StopRecording()
    {
        if (!IsRecording)
        {
            return;
        }
        markStopRecording = true;
    }

    /// <summary>
    /// Loop for buffering incoming audio data and delivering frames
    /// </summary>
    IEnumerator RecordData()
    {
        var sampleBuffer = new float[FrameLength];
        var startReadPos = 0;
        OnRecordingStart?.Invoke();
        while (IsRecording)
        {
            var curClipPos = Microphone.GetPosition(CurrentDeviceName);
            if (curClipPos < startReadPos)
            {
                curClipPos += _audioClip.samples;
            }
            var samplesAvailable = curClipPos - startReadPos;
            if (samplesAvailable < FrameLength)
            {
                yield return null;
                continue;
            }
            var endReadPos = startReadPos + FrameLength;
            if (endReadPos > _audioClip.samples)
            {
                // Fragmented read (wraps around to beginning of clip)
                // Read bit at end of clip
                var numSamplesClipEnd = _audioClip.samples - startReadPos;
                var endClipSamples = new float[numSamplesClipEnd];
                _audioClip.GetData(endClipSamples, startReadPos);

                // Read bit at start of clip
                var numSamplesClipStart = endReadPos - _audioClip.samples;
                var startClipSamples = new float[numSamplesClipStart];
                _audioClip.GetData(startClipSamples, 0);

                // Combine to form full frame
                Buffer.BlockCopy(endClipSamples, 0, sampleBuffer, 0, numSamplesClipEnd);
                Buffer.BlockCopy(startClipSamples, 0, sampleBuffer, numSamplesClipEnd, numSamplesClipStart);
            }
            else
            {
                _audioClip.GetData(sampleBuffer, startReadPos);
            }
            startReadPos = endReadPos % _audioClip.samples;
            
            if (_autoDetect == false)
            {
                _transmit =_audioDetected = true;
            }
            else
            {
                var maxVolume = sampleBuffer.Prepend(0.0f).Max();
                if (maxVolume >= _minimumSpeakingSampleValue)
                {
                    _transmit= _audioDetected = true;
                    _timeAtSilenceBegan = Time.time;
                }
                else
                {
                    _transmit = false;
                    if (_audioDetected && Time.time - _timeAtSilenceBegan > _silenceTimer)
                    {
                        _audioDetected = false;
                    }
                }
            }
            if (_audioDetected)
            {
                _didDetect = true;
                var pcmBuffer = new short[sampleBuffer.Length];
                for (var i = 0; i < FrameLength; i++)
                {
                    pcmBuffer[i] = (short) Math.Floor(sampleBuffer[i] * short.MaxValue);
                }
                // Raise buffer event
                if (OnFrameCaptured != null && _transmit)
                {
                    OnFrameCaptured.Invoke(pcmBuffer);
                }
            }
            else
            {
                if (!_didDetect)
                {
                    continue;
                }
                OnRecordingStop?.Invoke();
                _didDetect = false;
            }
            if (markStopRecording)
            {
                SaveRecording();
                Microphone.End(CurrentDeviceName);
                Destroy(_audioClip);
                _audioClip = null;
                _didDetect = false;
                yield break;
            }
        }

        OnRecordingStop?.Invoke();
        RestartRecording?.Invoke();
    }
    
    #region Recorder Functions
    public void SaveRecording(string fileName = "Audio")
        {
            samplesData = new float[_audioClip.samples * _audioClip.channels];
            _audioClip.GetData(samplesData, 0);
            
            // Trim the silence at the end of the recording
            var samples = samplesData.ToList();
            samplesData = samples.ToArray();
                
            // Create the audio file after removing the silence
            var audioClip = AudioClip.Create(fileName, samplesData.Length, _audioClip.channels, 16000, false);
            audioClip.SetData(samplesData, 0);
            // Assign Current Audio Clip to Audio Player
            var filePath = Path.Combine(Application.persistentDataPath, fileName + "_" + DateTime.UtcNow.ToString("yyyy_MM_dd_HH_mm_ss_ffff") + ".wav");
            
            // Delete the file if it exists.
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
            try
            {
                WriteWavFile(audioClip, filePath);
                Debug.Log("File Saved Successfully at " + filePath);
                OnRecordSaved?.Invoke(filePath);
            }
            catch (DirectoryNotFoundException)
            {
                Debug.LogError("Persistent Data Path not found!");
            }
        }

        // WAV file format from http://soundfile.sapp.org/doc/WaveFormat/
        static void WriteWavFile(AudioClip clip, string filePath)
        {
            var clipData = new float[clip.samples];

            //Create the file.
            using Stream fs = File.Create(filePath);
            var frequency = clip.frequency;
            var numOfChannels = clip.channels;
            var samples = clip.samples;
            fs.Seek(0, SeekOrigin.Begin);
            //Header
            // Chunk ID
            var riff = Encoding.ASCII.GetBytes("RIFF");
            fs.Write(riff, 0, 4);
            // ChunkSize
            var chunkSize = BitConverter.GetBytes((HEADER_SIZE + clipData.Length) - 8);
            fs.Write(chunkSize, 0, 4);
            // Format
            var wave = Encoding.ASCII.GetBytes("WAVE");
            fs.Write(wave, 0, 4);
            // Subchunk1ID
            var fmt = Encoding.ASCII.GetBytes("fmt ");
            fs.Write(fmt, 0, 4);
            // Subchunk1Size
            var subChunk1 = BitConverter.GetBytes(16);
            fs.Write(subChunk1, 0, 4);

            // AudioFormat
            var audioFormat = BitConverter.GetBytes(1);
            fs.Write(audioFormat, 0, 2);

            // NumChannels
            var numChannels = BitConverter.GetBytes(numOfChannels);
            fs.Write(numChannels, 0, 2);

            // SampleRate
            var sampleRate = BitConverter.GetBytes(frequency);
            fs.Write(sampleRate, 0, 4);

            // ByteRate
            var byteRate = BitConverter.GetBytes(frequency * numOfChannels * 2); // sampleRate * bytesPerSample*number of channels, here 44100*2*2
            fs.Write(byteRate, 0, 4);

            // BlockAlign
            ushort blockAlign = (ushort)(numOfChannels * 2);
            fs.Write(BitConverter.GetBytes(blockAlign), 0, 2);

            // BitsPerSample
            ushort bps = 16;
            var bitsPerSample = BitConverter.GetBytes(bps);
            fs.Write(bitsPerSample, 0, 2);

            // Subchunk2ID
            var datastring = Encoding.ASCII.GetBytes("data");
            fs.Write(datastring, 0, 4);

            // Subchunk2Size
            var subChunk2 = BitConverter.GetBytes(samples * numOfChannels * 2);
            fs.Write(subChunk2, 0, 4);

            // Data
            clip.GetData(clipData, 0);
            var intData = new short[clipData.Length];
            var bytesData = new byte[clipData.Length * 2];
            int convertionFactor = 32767;

            for (var i = 0; i < clipData.Length; i++)
            {
                intData[i] = (short)(clipData[i] * convertionFactor);
                var byteArr = new byte[2];
                byteArr = BitConverter.GetBytes(intData[i]);
                byteArr.CopyTo(bytesData, i * 2);
            }
            fs.Write(bytesData, 0, bytesData.Length);
        }

        #endregion
}
