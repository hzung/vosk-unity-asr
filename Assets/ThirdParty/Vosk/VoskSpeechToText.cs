using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Ionic.Zip;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Networking;
using Vosk;

public class VoskSpeechToText : MonoBehaviour
{
	[Tooltip("Location of the model, relative to the Streaming Assets folder.")]
	public string ModelPath = "";

	[Tooltip("The source of the microphone input.")]
	public VoiceProcessor voiceProcessor;
	
	[Tooltip("The Max number of alternatives that will be processed.")]
	public int MaxAlternatives = 3;

	[Tooltip("How long should we record before restarting?")]
	public float MaxRecordLength = 5;

	[Tooltip("Should the recognizer start when the application is launched?")]
	public bool AutoStart = true;

	[Tooltip("The phrases that will be detected. If left empty, all words will be detected.")]
	public List<string> KeyPhrases = new List<string>();

	//Cached version of the Vosk Model.
	private Model _model;

	//Cached version of the Vosk recognizer.
	private VoskRecognizer _recognizer;

	//Conditional flag to see if a recognizer has already been created.
	private bool _recognizerReady;

	//Holds all of the audio data until the user stops talking.
	private readonly List<short> _buffer = new List<short>();

	//Called when the the state of the controller changes.
	public Action<string> OnStatusUpdated;

	//Called after the user is done speaking and vosk processes the audio.
	public Action<string> OnTranscriptionResult;
	public Action<string> OnRecordSaved;
	public Action<bool> OnDetectSpeaking;
	public Action OnReady;

	//The absolute path to the decompressed model folder.
	private string _decompressedModelPath;

	//A string that contains the keywords in Json Array format
	private string _grammar = "";

	//Flag that is used to wait for the model file to decompress successfully.
	private bool _isDecompressing;

	//Flag that is used to wait for the the script to start successfully.
	private bool _isInitializing;

	//Flag that is used to check if Vosk was started.
	private bool _didInit;

	//Threading Logic

	// Flag to signal we are ending
	private bool _running;

	//Thread safe queue of microphone data.
	private readonly ConcurrentQueue<short[]> _threadedBufferQueue = new();
	//Thread safe queue of resuts
	private readonly ConcurrentQueue<string> _threadedResultQueue = new();
	static readonly ProfilerMarker voskRecognizerCreateMarker = new("VoskRecognizer.Create");
	static readonly ProfilerMarker voskRecognizerReadMarker = new("VoskRecognizer.AcceptWaveform");

	private void OnEnable()
	{
		voiceProcessor.OnFrameCaptured += VoiceProcessorOnOnFrameCaptured;
		voiceProcessor.OnRecordingStop += VoiceProcessorOnOnRecordingStop;
		voiceProcessor.OnRecordSaved += OnRecordSavedCallBack;
		voiceProcessor.OnDetectSpeaking += OnDetectSpeakingCallBack;
	}

	private void OnDisable()
	{
		voiceProcessor.OnFrameCaptured -= VoiceProcessorOnOnFrameCaptured;
		voiceProcessor.OnRecordingStop -= VoiceProcessorOnOnRecordingStop;
		voiceProcessor.OnRecordSaved -= OnRecordSavedCallBack;
		voiceProcessor.OnDetectSpeaking -= OnDetectSpeakingCallBack;
	}

	private void OnDetectSpeakingCallBack(bool isSpeaking)
	{
		OnDetectSpeaking?.Invoke(isSpeaking);
	}

	/// <summary>
	/// Start Vosk Speech to text
	/// </summary>
	/// <param name="keyPhrases">A list of keywords/phrases. Keywords need to exist in the models dictionary, so some words like "webview" are better detected as two more common words "web view".</param>
	/// <param name="modelPath">The path to the model folder relative to StreamingAssets. If the path has a .zip ending, it will be decompressed into the application data persistent folder.</param>
	/// <param name="startMicrophone">"Should the microphone after vosk initializes?</param>
	/// <param name="maxAlternatives">The maximum number of alternative phrases detected</param>
	public void Init(List<string> keyPhrases = null, string modelPath = default, int maxAlternatives = 3)
	{
		if (_isInitializing)
		{
			Debug.LogError("Initializing in progress!");
			return;
		}
		if (_didInit)
		{
			Debug.LogError("Vosk has already been initialized!");
			return;
		}

		if (!string.IsNullOrEmpty(modelPath))
		{
			ModelPath = modelPath;
		}

		if (keyPhrases != null)
		{
			KeyPhrases = keyPhrases;
		}

		MaxAlternatives = maxAlternatives;
		StartCoroutine(DoStartVoskStt());
	}

	//Decompress model, load settings, start Vosk and optionally start the microphone
	private IEnumerator DoStartVoskStt()
	{
		_isInitializing = true;
		yield return Decompress();
		OnStatusUpdated?.Invoke("Loading Model from: " + _decompressedModelPath);
		_model = new Model(_decompressedModelPath);
		OnStatusUpdated?.Invoke("Initialized");
		_isInitializing = false;
		_didInit = true;
		_running = true;
		Task.Run(ThreadedWork).ConfigureAwait(false);
		OnReady?.Invoke();
	}

	private void OnRecordSavedCallBack(string filePath)
	{
		OnRecordSaved?.Invoke(filePath);
	}

	public void StartRecording()
	{
		voiceProcessor.StartRecording();
	}

	public void StopRecording()
	{
		voiceProcessor.StopRecording();
	}

	// Translates the KeyPhraseses into a json array and appends the `[unk]` keyword at the end to tell vosk to filter other phrases.
	private void UpdateGrammar()
	{
		if (KeyPhrases.Count == 0)
		{
			_grammar = "";
			return;
		}
		var keywords = new JSONArray();
		foreach (string keyphrase in KeyPhrases)
		{
			keywords.Add(new JSONString(keyphrase.ToLower()));
		}

		keywords.Add(new JSONString("[unk]"));

		_grammar = keywords.ToString();
	}

	//Decompress the model zip file or return the location of the decompressed files.
	private IEnumerator Decompress()
	{
		if (!Path.HasExtension(ModelPath) || Directory.Exists(Path.Combine(Application.persistentDataPath, Path.GetFileNameWithoutExtension(ModelPath))))
		{
			OnStatusUpdated?.Invoke("Using existing decompressed model.");
			_decompressedModelPath = Path.Combine(Application.persistentDataPath, Path.GetFileNameWithoutExtension(ModelPath) ?? string.Empty);
			Debug.Log(_decompressedModelPath);
			yield break;
		}

		OnStatusUpdated?.Invoke("Decompressing model...");
		if (ModelPath != null)
		{
			string dataPath = Path.Combine(Application.streamingAssetsPath, ModelPath);

			Stream dataStream;
			// Read data from the streaming assets path. You cannot access the streaming assets directly on Android.
			if (dataPath.Contains("://"))
			{
				UnityWebRequest www = UnityWebRequest.Get(dataPath);
				www.SendWebRequest();
				while (!www.isDone)
				{
					yield return null;
				}
				dataStream = new MemoryStream(www.downloadHandler.data);
			}
			// Read the file directly on valid platforms.
			else
			{
				dataStream = File.OpenRead(dataPath);
			}

			//Read the Zip File
			var zipFile = ZipFile.Read(dataStream);

			//Listen for the zip file to complete extraction
			zipFile.ExtractProgress += ZipFileOnExtractProgress;

			//Update status text
			OnStatusUpdated?.Invoke("Reading Zip file");

			//Start Extraction
			zipFile.ExtractAll(Application.persistentDataPath);

			//Wait until it's complete
			while (_isDecompressing == false)
			{
				yield return null;
			}
			//Override path given in ZipFileOnExtractProgress to prevent crash
			_decompressedModelPath = Path.Combine(Application.persistentDataPath, Path.GetFileNameWithoutExtension(ModelPath));
			//Update status text
			OnStatusUpdated?.Invoke("Decompressing complete!");
			
			//Dispose the zipfile reader.
			zipFile.Dispose();
		}
	}

	///The function that is called when the zip file extraction process is updated.
	private void ZipFileOnExtractProgress(object sender, ExtractProgressEventArgs e)
	{
		if (e.EventType == ZipProgressEventType.Extracting_AfterExtractAll)
		{
			_isDecompressing = true;
			_decompressedModelPath = e.ExtractLocation;
		}
	}

	//Calls the On Phrase Recognized event on the Unity Thread
	void Update()
	{
		if (_threadedResultQueue.TryDequeue(out string voiceResult))
		{
		    OnTranscriptionResult?.Invoke(voiceResult);
		}
	}

	//Callback from the voice processor when new audio is detected
	private void VoiceProcessorOnOnFrameCaptured(short[] samples)
	{
        _threadedBufferQueue.Enqueue(samples);
	}

	//Callback from the voice processor when recording stops
	private void VoiceProcessorOnOnRecordingStop()
	{
        Debug.Log("Stopped");
	}

	//Feeds the autio logic into the vosk recorgnizer
	private async Task ThreadedWork()
	{
		voskRecognizerCreateMarker.Begin();
		if (!_recognizerReady)
		{
			UpdateGrammar();
			//Only detect defined keywords if they are specified.
			if (string.IsNullOrEmpty(_grammar))
			{
				_recognizer = new VoskRecognizer(_model, 16000.0f);
			}
			else
			{
				_recognizer = new VoskRecognizer(_model, 16000.0f, _grammar);
			}
			_recognizer.SetMaxAlternatives(MaxAlternatives);
			//_recognizer.SetWords(true);
			_recognizerReady = true;
		}
		voskRecognizerCreateMarker.End();
		voskRecognizerReadMarker.Begin();

		while (_running)
		{
			if (_threadedBufferQueue.TryDequeue(out short[] voiceResult))
			{
				if(_recognizer.AcceptWaveform(voiceResult, voiceResult.Length)) {
					var result = _recognizer.Result();
					_threadedResultQueue.Enqueue(result);
				}
			}
			else
			{
				await Task.Delay(100);
			}
			if (voiceProcessor.markStopRecording)
			{
				var result = _recognizer.Result();
				_threadedResultQueue.Enqueue(result);
				voiceProcessor.markStopRecording = false;
			}
		}
		voskRecognizerReadMarker.End();
	}
}
