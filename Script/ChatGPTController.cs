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

#if UNITY_IOS && !UNITY_EDITOR
    [DllImport("__Internal")] private static extern void ChatGPTBridge_SetupSpeech(string target, string method);
    [DllImport("__Internal")] private static extern void ChatGPTBridge_RequestPermissions();
    [DllImport("__Internal")] private static extern void ChatGPTBridge_StartSpeech();
    [DllImport("__Internal")] private static extern void ChatGPTBridge_StopSpeech();
    [DllImport("__Internal")] private static extern void ChatGPTBridge_Speak(string text);
    // A案：復活
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
            displayText.text = "Listening...";
            ChatGPTBridge_StartSpeech();
            isRecording = true;
        }
        else
        {
            ChatGPTBridge_StopSpeech();
            isRecording = false;
        }
    }

    // ネイティブ→Unity：音声認識の途中/最終結果（表示のみ・読み上げなし）
    public void OnSpeechPartial(string recognizedText)
    {
        if (displayText) displayText.text = recognizedText;
    }

    // 送信：画面のテキストをChatGPTへ
    public void SendQuestion()
    {
        var question = displayText ? displayText.text : "";
        if (!string.IsNullOrEmpty(question))
        {
            if (displayText) displayText.text = "Thinking...";
            ChatGPTBridge_Ask(question);
        }
    }

    // ChatGPT応答：必ず読み上げ
    public void OnGPTReply(string reply)
    {
        if (displayText) displayText.text = reply;
        //ChatGPTBridge_Speak(reply);   // ← 応答のみ必須で読み上げ
    }

    public void OnSpeechPermission(string status)
    {
        // status: "granted" or "denied" が飛んできます（iOS側実装どおり）
        Debug.Log($"[Bridge] Microphone permission: {status}");

        if (displayText) displayText.text =
            status == "granted" ? "マイク権限: 許可" : "マイク権限: 拒否";

        // もし許可されたら自動で録音開始したい場合はコメントアウト外す
        // if (status == "granted" && !isRecording)
        // {
        //     ChatGPTBridge_StartSpeech();
        //     isRecording = true;
        // }
    }

    // これを追加（iOSの willSpeak... → Swift → Unity SendMessage で呼ばれる）
    public void OnTTSRange(string json)
    {
        Debug.Log("[Unity][Lip] OnTTSRange受信: " + json);
        if (mouth) mouth.OnTTSRange(json);
    }
    
    public void OnSpeechError(string message)
    {
        Debug.LogError($"[Bridge] Speech error: {message}");
        if (displayText) displayText.text = $"音声入力エラー: {message}";
        // 必要なら録音フラグも落とす
        // isRecording = false;
    }
}