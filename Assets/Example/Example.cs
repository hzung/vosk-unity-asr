using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

public class Example : MonoBehaviour
{
    public Text txtResult;
    public Text txtButtonRecording;
    public Button btnPlay;
    public AudioSource audioSource;
    public VoskSpeechToText voskSpeechToText;
    private bool isReady = false;
    private string savedFilePath = string.Empty;

    private void OnEnable()
    {
        voskSpeechToText.OnReady += OnReady;
        voskSpeechToText.OnTranscriptionResult += OnTranscriptionResult;
        voskSpeechToText.OnRecordSaved += OnRecordSaved;
        voskSpeechToText.OnDetectSpeaking += OnDetectSpeaking;
    }

    private void OnDisable()
    {
        voskSpeechToText.OnReady -= OnReady;
        voskSpeechToText.OnTranscriptionResult -= OnTranscriptionResult;
        voskSpeechToText.OnRecordSaved -= OnRecordSaved;
        voskSpeechToText.OnDetectSpeaking -= OnDetectSpeaking;
    }

    void Awake()
    {
        voskSpeechToText.Init();
        txtButtonRecording.text = "Start";
    }

    private void OnDetectSpeaking(bool isSpeaking)
    {
        Debug.Log("Is Speaking: " + isSpeaking);
    }

    private void OnReady()
    {
        Debug.Log("Model is ready!");
        isReady = true;
    }

    private void OnTranscriptionResult(string result)
    {
        txtResult.text = result;
    }

    private void OnRecordSaved(string filePath)
    {
        Debug.Log("Saved: " + filePath);
        savedFilePath = filePath;
        btnPlay.gameObject.SetActive(true);
    }

    public void Trigger()
    {
        if (!isReady)
        {
            return;
        }
        if (voskSpeechToText.voiceProcessor.IsRecording)
        {
            voskSpeechToText.StopRecording();
            txtButtonRecording.text = "Start";
        }
        else
        {
            btnPlay.gameObject.SetActive(false);
            txtButtonRecording.text = "Stop";
            voskSpeechToText.StartRecording();
        }
    }

    public void PlaySavedAudio()
    {
        Debug.Log($"Play: {savedFilePath}");
        StartCoroutine(LoadFile(savedFilePath, audioClip =>
        {
            audioSource.PlayOneShot(audioClip);
        }, () =>
        {
            Debug.Log("Play audio fail");
        }));
    }

    private IEnumerator LoadFile(string path, Action<AudioClip> done, Action fail = null)
    {
        if (!System.IO.File.Exists(path))
        {
            yield break;
        }
        using var www = UnityWebRequestMultimedia.GetAudioClip("file://" + path, AudioType.WAV);
        yield return www.SendWebRequest();
        if (www.isDone)
        {
            var temp = DownloadHandlerAudioClip.GetContent(www);
            done?.Invoke(temp);
        }
        else
        {
            fail?.Invoke();
        }
    }
}