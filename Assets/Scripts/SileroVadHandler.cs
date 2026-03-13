using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.InferenceEngine;
using TMPro;

/// <summary>
/// Handles speech detection using the Silero VAD model.
/// </summary>
public class SileroVadHandler : MonoBehaviour
{
    [Header("Model")]
    public ModelAsset modelAsset;

    [Header("Speech Detection")]
    [Range(0f, 1f)] public float speechStartThreshold = 0.50f;
    [Range(0f, 1f)] public float speechEndThreshold = 0.35f;
    public float endSilenceSeconds = 0.50f;

    [Header("Pre-Roll")]
    [Tooltip("Amount of audio kept before speech start, in seconds.")]
    public float preRollSeconds = 0.3f;

    [Header("Realtime API")]
    public RealtimeAPIHandler realtimeHandler;

    [Header("Debug")]
    public bool logProbabilities = true;
    public bool logStateChanges = true;

    [Header("UI")]
    public TMP_Text VadActivityText;
    public TMP_Text probabilityText;
    public TMP_Text vadStatusText;
    public int maxDisplayedLogs = 30;

    private readonly Queue<string> uiLogs = new Queue<string>();

    private const int TargetSampleRate = 16000;
    private const int ChunkSize = 512;
    private const int StateElementCount = 2 * 1 * 64;
    private const float ChunkDurationSeconds = ChunkSize / 16000f;

    private Worker worker;
    private Tensor<float> xTensor;
    private Tensor<float> hTensor;
    private Tensor<float> cTensor;

    private string microphoneDevice = null;
    private int sampleRate = 16000;              // Desired sample rate; 16000 recommended.
    private int micLengthSec = 10;               // Length of the microphon loop; 10 ~ 15 seconds is recommended.
    private AudioClip micClip;
    private int micClipSamples;
    private int lastReadPos;
    private bool micRunning;

    private float[] pcmReadBuffer = Array.Empty<float>();
    private float[] resampleCarry = Array.Empty<float>();
    private readonly List<float> mono16kBuffer = new List<float>(4096);

    // Speech state
    private bool speechStarted = false;
    private bool isSpeaking = false;
    private float silenceTimer = 0f;

    // Captured utterance at 16 kHz mono
    private readonly List<float> capturedSpeechSamples = new List<float>(16000 * 10);

    // Rolling pre-roll buffer at 16 kHz mono
    private readonly List<float> preRollBuffer = new List<float>(4800);

    private int PreRollSampleCount => Mathf.Max(0, Mathf.RoundToInt(preRollSeconds * TargetSampleRate));

    void Start()
    {
        if (modelAsset == null)
        {
            LogErrorMessage("Model Asset is not assigned.");
            enabled = false;
            return;
        }

        Model runtimeModel = ModelLoader.Load(modelAsset);
        worker = new Worker(runtimeModel, BackendType.CPU);

        xTensor = new Tensor<float>(new TensorShape(1, ChunkSize), clearOnInit: true);
        hTensor = new Tensor<float>(new TensorShape(2, 1, 64), clearOnInit: true);
        cTensor = new Tensor<float>(new TensorShape(2, 1, 64), clearOnInit: true);

        AddUILog("App started.");
        UpdateProbabilityText(0f);
        UpdateVadStatusText("VAD Status: Waiting for speech");

        StartMic();
    }

    void Update()
    {
        if (!micRunning || micClip == null)
            return;

        int currentPos = Microphone.GetPosition(microphoneDevice);
        if (currentPos < 0)
            return;

        int newSamples = GetNewSampleCount(currentPos, lastReadPos, micClipSamples);
        if (newSamples <= 0)
            return;

        ReadNewMicSamples(lastReadPos, newSamples);
        lastReadPos = currentPos;

        while (mono16kBuffer.Count >= ChunkSize && micRunning)
        {
            float[] chunk = new float[ChunkSize];
            mono16kBuffer.CopyTo(0, chunk, 0, ChunkSize);
            mono16kBuffer.RemoveRange(0, ChunkSize);

            UpdatePreRollBuffer(chunk);

            float prob = RunVadStep(chunk);
            UpdateProbabilityText(prob);

            if (logProbabilities)
                LogMessage($"VAD prob = {prob:F6}", false);

            UpdateSpeechState(prob, chunk);
        }
    }

    public void StartMic()
    {
        if (Microphone.devices == null || Microphone.devices.Length == 0)
        {
            LogErrorMessage("No microphone device found.");
            return;
        }

        if (string.IsNullOrEmpty(microphoneDevice))
            microphoneDevice = Microphone.devices[0];

        micClip = Microphone.Start(microphoneDevice, true, micLengthSec, sampleRate);
        if (micClip == null)
        {
            LogErrorMessage("Microphone.Start failed.");
            return;
        }

        micClipSamples = micClip.samples;
        lastReadPos = 0;
        mono16kBuffer.Clear();
        resampleCarry = Array.Empty<float>();
        capturedSpeechSamples.Clear();
        preRollBuffer.Clear();

        micRunning = true;
        speechStarted = false;
        isSpeaking = false;
        silenceTimer = 0f;

        ResetStates();
        UpdateVadStatusText("VAD Status: Listening\nSilence Timer: 0.000s");

        if (logStateChanges)
            LogMessage($"Microphone started: {microphoneDevice}, clipHz={micClip.frequency}, channels={micClip.channels}");
    }

    public void StopMic()
    {
        if (!micRunning)
            return;

        Microphone.End(microphoneDevice);
        micRunning = false;

        if (logStateChanges)
            LogMessage("Microphone stopped.");
    }

    /// <summary>
    /// Updates the pre-roll buffer with the new chunk of audio samples.
    /// </summary>
    private void UpdatePreRollBuffer(float[] chunk)
    {
        if (speechStarted)
            return;

        int maxSamples = PreRollSampleCount;
        if (maxSamples <= 0)
            return;

        preRollBuffer.AddRange(chunk);

        int overflow = preRollBuffer.Count - maxSamples;
        if (overflow > 0)
            preRollBuffer.RemoveRange(0, overflow);
    }

    /// <summary>
    /// Updates the speech state based on the probability of speech and the chunk of audio samples.
    /// </summary>
    private void UpdateSpeechState(float prob, float[] chunk)
    {
        if (!speechStarted)
        {
            if (prob >= speechStartThreshold)
            {
                speechStarted = true;
                isSpeaking = true;
                silenceTimer = 0f;

                UpdateVadStatusText("VAD Status: Speech START detected\nSilence Timer: 0.000s");

                capturedSpeechSamples.Clear();

                if (preRollBuffer.Count > 0)
                    capturedSpeechSamples.AddRange(preRollBuffer);

                capturedSpeechSamples.AddRange(chunk);

                if (logStateChanges)
                {
                    float preRollSec = (float)preRollBuffer.Count / TargetSampleRate;
                    LogMessage($"Speech START detected. prob={prob:F6}, prepended pre-roll={preRollSec:F3}s");
                }
            }

            return;
        }

        if (isSpeaking)
        {
            capturedSpeechSamples.AddRange(chunk);

            if (prob >= speechEndThreshold)
            {
                silenceTimer = 0f;
                UpdateVadStatusText("VAD Status: Speaking\nSilence Timer: 0.000s");
            }
            else
            {
                silenceTimer += ChunkDurationSeconds;
                UpdateVadStatusText($"VAD Status: Possible speech end\nSilence Timer: {silenceTimer:F3}s");

                if (logStateChanges)
                    LogMessage($"Low prob during speech. silenceTimer={silenceTimer:F3}s", false);

                if (silenceTimer >= endSilenceSeconds)
                {
                    isSpeaking = false;
                    UpdateVadStatusText($"VAD Status: Speech END detected\nSilence Timer: {silenceTimer:F3}s");

                    if (logStateChanges)
                        LogMessage($"Speech END detected after {silenceTimer:F3}s silence.");

                    StopMic();

                    // Send captured speech to Realtime API; playback is handled by ResponseHandler/AudioUtility.
                    SendCapturedSpeechToRealtimeApi();
                }
            }
        }
    }

    /// <summary>
    /// Convert the captured 16 kHz mono speech samples to PCM16 base64 and send them to the RealtimeAPIHandler. 
    /// Only called after VAD has decided the user finished speaking.
    /// </summary>
    private void SendCapturedSpeechToRealtimeApi()
    {
        if (realtimeHandler == null)
            return;

        if (capturedSpeechSamples.Count == 0)
        {
            if (logStateChanges)
                LogMessage("No captured speech to send to Realtime API.");
            return;
        }
 
        float[] samples = capturedSpeechSamples.ToArray();
        string base64 = AudioUtility.ConvertFloatToPCM16AndBase64(samples);

        realtimeHandler.SendUserAudioBase64(base64);

        if (logStateChanges)
            LogMessage($"Sent captured speech to Realtime API. samples={samples.Length}, seconds={(float)samples.Length / TargetSampleRate:F2}");
    }

    private void ResetStates()
    {
        for (int i = 0; i < StateElementCount; i++)
        {
            hTensor[i] = 0f;
            cTensor[i] = 0f;
        }
    }

    private int GetNewSampleCount(int currentPos, int previousPos, int totalSamples)
    {
        if (currentPos >= previousPos)
            return currentPos - previousPos;

        return (totalSamples - previousPos) + currentPos;
    }

    private void ReadNewMicSamples(int startSample, int sampleCount)
    {
        if (sampleCount <= 0 || micClip == null)
            return;

        int channels = micClip.channels;
        int clipFreq = micClip.frequency;

        int samplesUntilEnd = micClipSamples - startSample;
        if (sampleCount <= samplesUntilEnd)
        {
            ReadSegment(startSample, sampleCount, channels, clipFreq);
        }
        else
        {
            ReadSegment(startSample, samplesUntilEnd, channels, clipFreq);
            ReadSegment(0, sampleCount - samplesUntilEnd, channels, clipFreq);
        }
    }

    private void ReadSegment(int startSample, int sampleCount, int channels, int clipFreq)
    {
        if (sampleCount <= 0)
            return;

        int totalFloats = sampleCount * channels;
        if (pcmReadBuffer.Length != totalFloats)
            pcmReadBuffer = new float[totalFloats];

        bool ok = micClip.GetData(pcmReadBuffer, startSample);
        if (!ok)
        {
            LogWarningMessage("AudioClip.GetData failed.");
            return;
        }

        float[] mono = new float[sampleCount];

        if (channels == 1)
        {
            Array.Copy(pcmReadBuffer, mono, sampleCount);
        }
        else
        {
            for (int i = 0; i < sampleCount; i++)
            {
                float sum = 0f;
                int baseIndex = i * channels;
                for (int ch = 0; ch < channels; ch++)
                    sum += pcmReadBuffer[baseIndex + ch];

                mono[i] = sum / channels;
            }
        }

        float[] mono16k = (clipFreq == TargetSampleRate)
            ? mono
            : ResampleLinear(mono, clipFreq, TargetSampleRate);

        mono16kBuffer.AddRange(mono16k);
    }

    private float[] ResampleLinear(float[] input, int srcRate, int dstRate)
    {
        if (srcRate == dstRate)
            return input;

        float[] working;
        if (resampleCarry.Length > 0)
        {
            working = new float[resampleCarry.Length + input.Length];
            Array.Copy(resampleCarry, 0, working, 0, resampleCarry.Length);
            Array.Copy(input, 0, working, resampleCarry.Length, input.Length);
        }
        else
        {
            working = input;
        }

        if (working.Length < 2)
            return Array.Empty<float>();

        double ratio = (double)dstRate / srcRate;
        int outLen = Mathf.Max(1, (int)Math.Floor(working.Length * ratio));
        float[] output = new float[outLen];

        for (int i = 0; i < outLen; i++)
        {
            double srcPos = i / ratio;
            int i0 = (int)Math.Floor(srcPos);
            int i1 = Mathf.Min(i0 + 1, working.Length - 1);
            float t = (float)(srcPos - i0);
            output[i] = Mathf.Lerp(working[i0], working[i1], t);
        }

        resampleCarry = new float[1];
        resampleCarry[0] = working[working.Length - 1];

        return output;
    }

    // Runtime model inference step.
    private float RunVadStep(float[] chunk512)
    {
        if (chunk512 == null || chunk512.Length != ChunkSize)
        {
            LogErrorMessage($"Expected {ChunkSize} samples, got {chunk512?.Length ?? 0}.");
            return 0f;
        }

        for (int i = 0; i < ChunkSize; i++)
            xTensor[i] = chunk512[i];

        worker.SetInput("x", xTensor);
        worker.SetInput("h", hTensor);
        worker.SetInput("c", cTensor);
        worker.Schedule();

        var probTensor = worker.PeekOutput("prob") as Tensor<float>;
        var newHTensor = worker.PeekOutput("new_h") as Tensor<float>;
        var newCTensor = worker.PeekOutput("new_c") as Tensor<float>;

        using var probCpu = probTensor.ReadbackAndClone();
        using var newHCpu = newHTensor.ReadbackAndClone();
        using var newCCpu = newCTensor.ReadbackAndClone();

        float prob = probCpu[0];

        for (int i = 0; i < StateElementCount; i++)
        {
            hTensor[i] = newHCpu[i];
            cTensor[i] = newCCpu[i];
        }

        return prob;
    }

    private void AddUILog(string message)
    {
        string timeStamped = $"[{Time.time:F2}] {message}";
        uiLogs.Enqueue(timeStamped);

        while (uiLogs.Count > maxDisplayedLogs)
            uiLogs.Dequeue();

        if (VadActivityText != null)
            VadActivityText.text = string.Join("\n", uiLogs);
    }

    private void UpdateProbabilityText(float probability)
    {
        if (probabilityText != null)
            probabilityText.text = $"VAD Probability: {probability:F6}";
    }

    private void UpdateVadStatusText(string status)
    {
        if (vadStatusText != null)
            vadStatusText.text = status;
    }

    private void LogMessage(string message, bool alsoConsole = true)
    {
        if (alsoConsole)
            Debug.Log(message);

        AddUILog(message);
    }

    private void LogWarningMessage(string message)
    {
        Debug.LogWarning(message);
        AddUILog("[Warning] " + message);
    }

    private void LogErrorMessage(string message)
    {
        Debug.LogError(message);
        AddUILog("[Error] " + message);
    }

    void OnDisable()
    {
        StopMic();

        xTensor?.Dispose();
        hTensor?.Dispose();
        cTensor?.Dispose();
        worker?.Dispose();
    }
}