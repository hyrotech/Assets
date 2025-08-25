using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class ChatGPTController : MonoBehaviour
{
    [SerializeField] private TTSMouthDriver mouth; // ← Inspector で TTSMouthDriver をドラッグ
    [SerializeField] private TextMeshProUGUI displayText;
    [SerializeField] private Button recordButton;     // 録音トグル
    [SerializeField] private Button sendButton;       // 送信ボタン（Inspectorで割当て推奨）
    [SerializeField] private TTSPCMPlayer ttsPlayer;  // ★ PCM再生担当（BridgeTarget などに付ける）

#if UNITY_IOS && !UNITY_EDITOR
    [DllImport("__Internal")] private static extern void ChatGPTBridge_SetupSpeech(string target, string method);
    [DllImport("__Internal")] private static extern void ChatGPTBridge_RequestPermissions();
    [DllImport("__Internal")] private static extern void ChatGPTBridge_StartSpeech();
    [DllImport("__Internal")] private static extern void ChatGPTBridge_StopSpeech();
    [DllImport("__Internal")] private static extern void ChatGPTBridge_Speak(string text);
    [DllImport("__Internal")] private static extern void ChatGPTBridge_SetupChatGPT(string target, string method);
    [DllImport("__Internal")] private static extern void ChatGPTBridge_Ask(string question);
#else
    private static void ChatGPTBridge_SetupSpeech(string t, string m) { }
    private static void ChatGPTBridge_RequestPermissions() { }
    private static void ChatGPTBridge_StartSpeech() { }
    private static void ChatGPTBridge_StopSpeech() { }
    private static void ChatGPTBridge_Speak(string t) { }
    private static void ChatGPTBridge_SetupChatGPT(string t, string m) { }
    private static void ChatGPTBridge_Ask(string q) { }
#endif

    private bool isRecording = false;

    private void Start()
    {
        // 同じGOに TTSPCMPlayer があれば拾う（Inspector 未設定の保険）
        if (!ttsPlayer) ttsPlayer = GetComponent<TTSPCMPlayer>();

        // 音声入力の受け口
        ChatGPTBridge_SetupSpeech(gameObject.name, nameof(OnSpeechPartial));
        ChatGPTBridge_RequestPermissions();

        // ChatGPT応答の受け口
        ChatGPTBridge_SetupChatGPT(gameObject.name, nameof(OnGPTReply));

        if (recordButton) recordButton.onClick.AddListener(OnRecordButtonPressed);
        if (sendButton) sendButton.onClick.AddListener(SendQuestion);
    }

    private void OnDestroy()
    {
        if (recordButton) recordButton.onClick.RemoveListener(OnRecordButtonPressed);
        if (sendButton) sendButton.onClick.RemoveListener(SendQuestion);
    }

    private void OnRecordButtonPressed()
    {
        if (!isRecording)
        {
            if (displayText) displayText.text = "Listening...";
            ChatGPTBridge_StartSpeech();
            isRecording = true;
        }
        else
        {
            ChatGPTBridge_StopSpeech();
            isRecording = false;
        }
    }

    // ---------- iOS → Unity 受け口 ----------
    public void OnSpeechPartial(string recognizedText)
    {
        if (displayText) displayText.text = recognizedText;
    }

    public void OnGPTReply(string reply)
    {
        if (displayText) displayText.text = reply;
        // ChatGPTBridge_Speak(reply);
    }

    public void OnSpeechPermission(string status)
    {
        Debug.Log($"[Bridge] Microphone permission: {status}");
        if (displayText) displayText.text = status == "granted" ? "マイク権限: 許可" : "マイク権限: 拒否";
    }

    public void OnTTSRange(string json)
    {
        Debug.Log("[Unity][Lip] OnTTSRange受信: " + json);
        if (mouth) mouth.OnTTSRange(json);
    }

    public void OnSpeechError(string message)
    {
        Debug.LogError($"[Bridge] Speech error: {message}");
        if (displayText) displayText.text = $"音声入力エラー: {message}";
    }

    // ---------- ★ ここが質問の3メソッド（iOSのPCMストリーム受け口） ----------
    public void OnTTSStreamBegin(string json)
    {
        if (!ttsPlayer) ttsPlayer = GetComponent<TTSPCMPlayer>();
        if (!ttsPlayer) ttsPlayer = gameObject.AddComponent<TTSPCMPlayer>();
        ttsPlayer.OnTTSStreamBegin(json);
    }

    public void OnTTSStreamChunk(string json)
    {
        if (ttsPlayer) ttsPlayer.OnTTSStreamChunk(json);
    }

    public void OnTTSStreamEnd(string json)
    {
        if (ttsPlayer) ttsPlayer.OnTTSStreamEnd(json);
    }

    public void SendQuestion()
    {
        var question = displayText ? displayText.text : "";
        if (!string.IsNullOrEmpty(question))
        {
            if (displayText) displayText.text = "Thinking...";
            ChatGPTBridge_Ask(question);
        }
    }
}