using UnityEngine;
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Net.WebSockets;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;

/// <summary>
/// Handles WebSocket connection to OpenAI's Realtime API.
/// Current endpoint: wss://api.openai.com/v1/realtime?model=gpt-realtime (on Mar 13, 2026)
/// Docs: https://platform.openai.com/docs/guides/realtime-websocket
/// </summary>
public class RealtimeAPIHandler : MonoBehaviour
{
    // Change the endpoint if necessary.
    private const string RealtimeEndpoint = "wss://api.openai.com/v1/realtime?model=gpt-realtime";

    // Use your own API key.
    private string apiKey = "";

    public AudioUtility audioUtility;

    private ClientWebSocket _ws;
    private CancellationTokenSource _receiveCts;
    private bool _isConnected;

    public bool IsConnected => _ws != null && _ws.State == WebSocketState.Open;

    // Events 
    public static event Action OnConnected;
    public static event Action<string> OnConnectionFailed;
    public static event Action OnDisconnected;
    public static event Action<string> OnRawEventReceived;

    private bool _isResponseInProgress;

    private void Start()
    {
        Connect();
    }

    public async void Connect()
    {
        if (IsConnected)
        {
            Debug.LogWarning("RealtimeAPIHandler: Already connected.");
            return;
        }

        if (string.IsNullOrEmpty(apiKey))
        {
            OnConnectionFailed?.Invoke("API key is empty.");
            return;
        }

        _ws = new ClientWebSocket();
        _ws.Options.SetRequestHeader("Authorization", "Bearer " + apiKey);

        var uri = new Uri(RealtimeEndpoint);

        try
        {
            await _ws.ConnectAsync(uri, CancellationToken.None);

            if (_ws.State == WebSocketState.Open)
            {
                _isConnected = true;
                OnConnected?.Invoke();
                Debug.Log("RealtimeAPIHandler: Connected to " + RealtimeEndpoint);

                // Configure the session for Realtime API speech and audio output.
                await SendSessionUpdate();

                // Start background receive loop.
                _receiveCts = new CancellationTokenSource();
                _ = ReceiveLoop(_receiveCts.Token);
            }
            else
            {
                OnConnectionFailed?.Invoke("WebSocket did not open: " + _ws.State);
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("RealtimeAPIHandler: Connection failed: " + ex.Message);
            OnConnectionFailed?.Invoke(ex.Message);
            CleanupWebSocket();
        }
    }

    /// <summary>
    /// Receives messages until the socket closes or is cancelled.
    /// </summary>
    private async Task ReceiveLoop(CancellationToken cancel)
    {
        var buffer = new byte[1024 * 64];
        var messageBuffer = new StringBuilder();

        while (_ws != null && (_ws.State == WebSocketState.Open || _ws.State == WebSocketState.CloseReceived) && !cancel.IsCancellationRequested)
        {
            try
            {
                var segment = new ArraySegment<byte>(buffer);
                var result = await _ws.ReceiveAsync(segment, cancel);        
                messageBuffer.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                if (_ws.State == WebSocketState.CloseReceived)
                {
                    Debug.Log("RealtimeAPIHandler: Server requested close.");
                    break;
                }

                if (result.EndOfMessage)   
                {
                    string fullMessage = messageBuffer.ToString();
                    messageBuffer.Clear();
                    if (!string.IsNullOrWhiteSpace(fullMessage))
                    {
                        OnMessageReceived(fullMessage);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Debug.LogError("RealtimeAPIHandler: Receive error: " + ex.Message);
                break;
            }
        }

        if (_isConnected)
        {
            _isConnected = false;
            OnDisconnected?.Invoke();
        }

        CleanupWebSocket();
    }

    /// <summary>
    /// Called for each complete JSON message to handle the events.
    /// </summary>
    protected virtual void OnMessageReceived(string rawJson)
    {
        OnRawEventReceived?.Invoke(rawJson);

        try
        {
            var evt = JObject.Parse(rawJson);
            string type = evt["type"]?.ToString() ?? "(no type)";

            switch (type)
            {
                case "session.created":
                    Debug.Log("RealtimeAPIHandler: session.created");
                    break;

                case "response.created":
                    _isResponseInProgress = true;
                    Debug.Log("RealtimeAPIHandler: response.created");
                    break;

                case "response.output_audio.delta":
                    Debug.Log("RealtimeAPIHandler: received output_audio.delta chunk");
                    break;

                case "response.output_audio.done":
                    Debug.Log("RealtimeAPIHandler: output_audio.done");
                    break;

                case "response.output_audio_transcript.delta":
                    Debug.Log("RealtimeAPIHandler: output_audio_transcript.delta");
                    break;

                case "response.output_audio_transcript.done":
                    Debug.Log("RealtimeAPIHandler: output_audio_transcript.done");
                    break;

                case "response.done":
                    _isResponseInProgress = false;
                    Debug.Log("RealtimeAPIHandler: response.done");
                    break;

                case "error":
                    string message = evt["error"]?["message"]?.ToString();
                    Debug.LogError("RealtimeAPIHandler: error from server: " + message);
                    break;

                default:
                    Debug.Log("RealtimeAPIHandler: event type = " + type);
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("RealtimeAPIHandler: failed to parse event JSON: " + ex.Message);
            Debug.Log("RealtimeAPIHandler raw: " + (rawJson.Length > 200 ? rawJson.Substring(0, 200) + "..." : rawJson));
        }
    }

    /// <summary>
    /// Send initial session.update. Keep defaults for input audio format.
    /// </summary>
    private async Task SendSessionUpdate()
    {
        if (!IsConnected) return;

        var sessionUpdate = new
        {
            type = "session.update",
            session = new
            {
                type = "realtime",
                model = "gpt-realtime",
                audio = new
                {
                    output = new
                    {
                        voice = "marin"
                    }
                }
            }
        };

        string json = Newtonsoft.Json.JsonConvert.SerializeObject(sessionUpdate);
        await SendJsonAsync(json);
    }

    /// <summary>
    /// Send a base64-encoded PCM16 (16 kHz) snippet as a user message,
    /// then request an audio + text response from the Realtime API.
    /// </summary>
    public async void SendUserAudioBase64(string base64Audio)
    {
        if (!IsConnected || string.IsNullOrEmpty(base64Audio)) return;

        // 1) Create a conversation item with input_audio content.
        var conversationItemCreate = new
        {
            type = "conversation.item.create",
            item = new
            {
                type = "message",
                role = "user",
                content = new object[]
                {
                    new
                    {
                        type = "input_audio",
                        audio = base64Audio
                    }
                }
            }
        };

        string itemJson = Newtonsoft.Json.JsonConvert.SerializeObject(conversationItemCreate);
        await SendJsonAsync(itemJson);

        // Ask the model to create a response.
        // GA Realtime API: modalities are configured on the session (output_modalities), not on the response object, so we only send instructions here.
        var responseCreate = new
        {
            type = "response.create",
            response = new
            {
                instructions = "You are a helpful assistant."
            }
        };

        string responseJson = Newtonsoft.Json.JsonConvert.SerializeObject(responseCreate);
        await SendJsonAsync(responseJson);
    }

    /// <summary>
    /// Helper to send a JSON message over the WebSocket.
    /// </summary>
    private async Task SendJsonAsync(string json)
    {
        if (!IsConnected || string.IsNullOrEmpty(json)) return;

        var bytes = Encoding.UTF8.GetBytes(json);
        var segment = new ArraySegment<byte>(bytes);
        try
        {
            await _ws.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Debug.LogError("RealtimeAPIHandler: SendJsonAsync failed: " + ex.Message);
        }
    }

    private void CleanupWebSocket()
    {
        _ws?.Dispose();
        _ws = null;
        _isConnected = false;
        _receiveCts?.Dispose();
        _receiveCts = null;
    }

    /// <summary>
    /// Disconnect and release the WebSocket.
    /// </summary>
    public async void Disconnect()
    {
        _receiveCts?.Cancel();

        if (_ws == null) return;

        if (_ws.State == WebSocketState.Open || _ws.State == WebSocketState.CloseReceived)
        {
            try
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by client", CancellationToken.None);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("RealtimeAPIHandler: CloseAsync: " + ex.Message);
            }
        }

        CleanupWebSocket();
        OnDisconnected?.Invoke();
    }

    private void OnDestroy()
    {
        Disconnect();
    }

    private void OnApplicationQuit()
    {
        Disconnect();
    }
}
