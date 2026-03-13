# SileroVAD with RealtimeAPI in Unity

This project integrates a VAD (Voice Activity Detection) ONNX model with the OpenAI Realtime API, enabling low-latency and multi-modal conversation with speech detection.

Test with Unity version 6000.0.67f1 on Mar 13, 2026.

## VAD Model

This project uses the sherpa-onnx Silero VAD model(silero_vad.onnx exported by k2-fsa).

The VAD model runs on Sentis.

You can check the model from:  
https://k2-fsa.github.io/sherpa/onnx/vad/silero-vad.html#download-models-files

## Setup

1. Import this project into your own Unity project.
2. Open the `VAD_RealtimeAPI` scene.
3. Enter your own API key.
4. Run

## Reminder
Running on Windows, but never tested on Mac.

## Reference

This project was inspired by and references:  
https://github.com/mapluisch/OpenAI-Realtime-API-for-Unity




