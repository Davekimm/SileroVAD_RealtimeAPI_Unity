using UnityEngine;
using TMPro;
using System;
using Newtonsoft.Json.Linq;

/// <summary>
/// Listens to events from RealtimeAPIHandler and plays streaming audio.
/// </summary>
public class ResponseHandler : MonoBehaviour
{
    [Header("References")]
    public RealtimeAPIHandler realtimeHandler;
    public AudioUtility audioUtility;
    public SileroVadHandler sileroVad;
    public TMP_Text transcriptText;

    private string _fullTranscript = string.Empty;

    private void OnEnable()
    {
        RealtimeAPIHandler.OnRawEventReceived += HandleRawEvent;

        if (audioUtility != null)
        {
            audioUtility.OnPlaybackFinished += HandlePlaybackFinished;
        }
    }

    private void Start()
    {
        if (realtimeHandler == null)
        {
            realtimeHandler = FindObjectOfType<RealtimeAPIHandler>();
        }
        if (audioUtility == null)
        {
            audioUtility = FindObjectOfType<AudioUtility>(); 
            if (audioUtility != null)
            {
                audioUtility.OnPlaybackFinished += HandlePlaybackFinished;
            }
        }
        if (sileroVad == null)
        {
            sileroVad = FindObjectOfType<SileroVadHandler>();
        }
    }

    private void HandleRawEvent(string rawJson)
    {
        if (string.IsNullOrEmpty(rawJson)) return;

        try
        {
            var evt = JObject.Parse(rawJson);
            string type = evt["type"]?.ToString();
            if (string.IsNullOrEmpty(type)) return;

            switch (type)
            {
                case "response.output_audio.delta":
                    HandleAudioDelta(evt);
                    break;

                case "response.output_audio_transcript.delta":
                    HandleTranscriptDelta(evt);
                    break;

                case "response.created":
                    // Reset transcript at the start of each response
                    _fullTranscript = string.Empty;
                    if (transcriptText != null)
                        transcriptText.text = string.Empty;
                    break;

                case "response.output_audio_transcript.done":
                    // Nothing extra needed for now; _fullTranscript already has everything
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("ResponseHandler: Failed to parse event JSON: " + ex.Message);
        }
    }

    /// <summary>
    /// Handles the audio delta event from the RealtimeAPIHandler.
    /// </summary>
    private void HandleAudioDelta(JObject evt)
    {
        if (audioUtility == null) return;

        // GA naming: delta holds base64 PCM16 audio for output_audio
        string base64 = evt["delta"]?.ToString();
        if (string.IsNullOrEmpty(base64)) return;

        try
        {
            byte[] pcmBytes = Convert.FromBase64String(base64);
            audioUtility.EnqueueAudioData(pcmBytes);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("ResponseHandler: Failed to decode audio delta: " + ex.Message);
        }
    }

    /// <summary>
    /// Handles the transcript delta event from the RealtimeAPIHandler.
    /// </summary>
    private void HandleTranscriptDelta(JObject evt)
    {
        string delta = evt["delta"]?.ToString();
        if (string.IsNullOrEmpty(delta)) return;

        _fullTranscript += delta;
        if (transcriptText != null)
        {
            transcriptText.text = _fullTranscript;
        }
    }

    /// <summary>
    /// Called when AudioUtility has finished playing all buffered response audio.
    /// Then Restarts the microphone/VAD loop.
    /// </summary>
    private void HandlePlaybackFinished()
    {
        if (sileroVad != null)
        {
            sileroVad.StartMic();
        }
    }

    private void OnDisable()
    {
        RealtimeAPIHandler.OnRawEventReceived -= HandleRawEvent;

        if (audioUtility != null)
        {
            audioUtility.OnPlaybackFinished -= HandlePlaybackFinished;
        }
    }
}
