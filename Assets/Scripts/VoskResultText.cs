using UnityEngine;
using UnityEngine.UI;

public class VoskResultText : MonoBehaviour 
{
    public VoskSpeechToText VoskSpeechToText;
    public Text ResultText;

    void Awake()
    {
        VoskSpeechToText.OnTranscriptionResult += OnTranscriptionResult;
    }

    private void OnTranscriptionResult(string obj)
    {
        Debug.Log(obj);
        var result = new RecognitionResult(obj);
        var resultTxt = string.Empty;
        var highestConfident = float.MinValue;
        foreach (var resultPhrase in result.Phrases)
        {
            if (resultPhrase.Confidence > highestConfident)
            {
                highestConfident = resultPhrase.Confidence;
                resultTxt = resultPhrase.Text;
            }
        }
        ResultText.text = $"[{resultTxt}]";
    }
}
