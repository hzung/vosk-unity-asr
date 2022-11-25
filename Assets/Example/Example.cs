using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

public class Example : MonoBehaviour
{
    public Text txtResult;
    public Text txtButtonRecording;
    public AudioSource audioSource;
    public VoskSpeechToText voskSpeechToText;
    private bool isReady = false;
    private string savedFilePath = string.Empty;
    private List<string> results = new List<string>();

    private void OnEnable()
    {
        voskSpeechToText.OnReady += OnReady;
        voskSpeechToText.OnTranscriptionResult += OnTranscriptionResult;
        voskSpeechToText.OnRecordSaved += OnRecordSaved;
    }

    private void OnDisable()
    {
        voskSpeechToText.OnReady -= OnReady;
        voskSpeechToText.OnTranscriptionResult -= OnTranscriptionResult;
        voskSpeechToText.OnRecordSaved -= OnRecordSaved;
    }

    void Awake()
    {
        voskSpeechToText.Init();
        txtButtonRecording.text = "Start";
    }

    private void OnReady()
    {
        Debug.Log("Model is ready!");
        isReady = true;
    }

    private void OnTranscriptionResult(string result)
    {
        results.Add(result);
        var totalResult = results.Aggregate(string.Empty, (current, r) => current + (r + "\n"));
        txtResult.text = totalResult;
    }

    private void OnRecordSaved(string filePath)
    {
        Debug.Log("Saved: " + filePath);
        savedFilePath = filePath;
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
            txtButtonRecording.text = "Stop";
            voskSpeechToText.StartRecording();
        }
    }

    public void PlaySavedAudio()
    {
        Debug.Log($"Play: {savedFilePath}");
    }
}
