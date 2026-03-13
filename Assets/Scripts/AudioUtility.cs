using System;
using System.Collections.Generic;
using System.Collections;
using UnityEngine;

/// <summary>
/// Unified audio helper
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class AudioUtility : MonoBehaviour
{
    /// <summary>Convert PCM16 bytes to float samples in [-1, 1].</summary>
    public static float[] ConvertPCM16ToFloat(byte[] pcmAudioData)
    {
        int length = pcmAudioData.Length / 2;
        float[] floatData = new float[length];
        for (int i = 0; i < length; i++)
        {
            short sample = BitConverter.ToInt16(pcmAudioData, i * 2);
            floatData[i] = sample / 32768f;
        }
        return floatData;
    }

    /// <summary>Convert float samples in [-1, 1] to base64-encoded PCM16 bytes.</summary>
    public static string ConvertFloatToPCM16AndBase64(float[] audioData)
    {
        byte[] pcm16Audio = new byte[audioData.Length * 2];
        for (int i = 0; i < audioData.Length; i++)
        {
            short value = (short)(Mathf.Clamp(audioData[i], -1f, 1f) * short.MaxValue);
            pcm16Audio[i * 2] = (byte)(value & 0xFF);
            pcm16Audio[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
        }
        return Convert.ToBase64String(pcm16Audio);
    }

    private int sampleRate = 24000;
    private const int BUFFER_SIZE = 48000;
    private const float MIN_BUFFER_TIME = 0.1f;

    private AudioSource _audioSource;
    private readonly List<float> _audioBuffer = new List<float>();
    private AudioClip _playbackClip;
    private bool _isPlayingAudio;
    private bool _cancelPending;

    /// <summary>Invoked when buffered playback fully finishes (no more data and AudioSource stopped).</summary>
    public event Action OnPlaybackFinished;

    private void Start()
    {
        _audioSource = GetComponent<AudioSource>();
        _audioSource.loop = false;
    }

    /// <summary>Queue PCM16 bytes for playback.</summary>
    public void EnqueueAudioData(byte[] pcmAudioData)
    {
        if (_cancelPending) return;

        float[] floatData = ConvertPCM16ToFloat(pcmAudioData);
        _audioBuffer.AddRange(floatData);

        if (!_isPlayingAudio)
        {
            StartCoroutine(PlayAudioCoroutine());
        }
    }

    /// <summary>Plays the audio response data from the buffer.</summary>
    private IEnumerator PlayAudioCoroutine()
    {
        _isPlayingAudio = true;
        while (_isPlayingAudio)
        {
            if (_audioBuffer.Count >= sampleRate * MIN_BUFFER_TIME)
            {
                int samplesToPlay = Mathf.Min(BUFFER_SIZE, _audioBuffer.Count);
                float[] audioChunk = new float[samplesToPlay];
                _audioBuffer.CopyTo(0, audioChunk, 0, samplesToPlay);
                _audioBuffer.RemoveRange(0, samplesToPlay);

                _playbackClip = AudioClip.Create("PlaybackClip", samplesToPlay, 1, sampleRate, false);
                _playbackClip.SetData(audioChunk, 0);
                _audioSource.clip = _playbackClip;
                _audioSource.Play();
                yield return new WaitForSeconds((float)samplesToPlay / sampleRate);
            }
            else if (_audioBuffer.Count > 0)
            {
                float[] audioChunk = _audioBuffer.ToArray();
                _audioBuffer.Clear();
                _playbackClip = AudioClip.Create("PlaybackClip", audioChunk.Length, 1, sampleRate, false);
                _playbackClip.SetData(audioChunk, 0);
                _audioSource.clip = _playbackClip;
                _audioSource.Play();
                yield return new WaitForSeconds((float)audioChunk.Length / sampleRate);
            }
            else if (_audioBuffer.Count == 0 && !_audioSource.isPlaying)
            {
                yield return new WaitForSeconds(0.1f);
                if (_audioBuffer.Count == 0) _isPlayingAudio = false;
            }
            else
            {
                yield return null;
            }
        }

        ClearAudioBuffer();

        // Notify that playback has fully finished to turn the mic on.
        OnPlaybackFinished?.Invoke();
    }

    public void CancelAudioPlayback()
    {
        _cancelPending = true;
        StopAllCoroutines();
        ClearAudioBuffer();
    }

    public void ClearAudioBuffer()
    {
        _audioBuffer.Clear();
        _audioSource.Stop();
        _isPlayingAudio = false;
    }

    public bool IsAudioPlaying()
    {
        return _audioSource.isPlaying || _audioBuffer.Count > 0;
    }

    public void ResetCancelPending()
    {
        _cancelPending = false;
    }
}
