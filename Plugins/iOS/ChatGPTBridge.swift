import Foundation
import UIKit
import AVFoundation
import Speech
import AVFAudio

// ---- Tiny logger ----
enum BridgeLog {
    static var enabled: Bool = true
    static func d(_ tag: String, _ msg: String) { guard enabled else { return }; print("[Bridge][\(tag)] \(msg)") }
    static func e(_ tag: String, _ msg: String) { print("[Bridge][ERR][\(tag)] \(msg)") }
}

// =======================
// Unity Bridge 基本
// =======================
@_silgen_name("UnitySendMessage")
private func UnitySendMessage(_ obj: UnsafePointer<CChar>, _ method: UnsafePointer<CChar>, _ msg: UnsafePointer<CChar>)

@inline(__always)
fileprivate func sendUnity(_ go: String, _ method: String, _ message: String) {
    go.withCString { g in method.withCString { m in message.withCString { s in
        UnitySendMessage(g, m, s)
    }}}
}

// 送信先を一元管理
fileprivate enum UnityCallback {
    static var gameObject: String = "BridgeTarget"
    static var onSpeech:   String = "OnSpeechPartial"
    static var onReply:    String = "OnChatGPTReply"
}

// =======================
// Speech to Text
// =======================
final class SpeechManager {
    static let shared = SpeechManager()

    private let engine = AVAudioEngine()
    private var request: SFSpeechAudioBufferRecognitionRequest?
    private var task: SFSpeechRecognitionTask?
    private let recognizer = SFSpeechRecognizer(locale: Locale(identifier: "ja-JP"))

    private(set) var isRunning = false

    // 発話ごとの一時バッファ
    private(set) var currentTranscript: String = ""
    private var lastFinalText: String?

    func clearBufferForNextUtterance() {
        currentTranscript = ""
        lastFinalText = nil
    }
    func takeFinalTranscript() -> String? {
        let t = lastFinalText
        lastFinalText = nil
        return t
    }

    func requestPermissions(_ completion: @escaping (Bool)->Void) {
        if #available(iOS 17.0, *) {
            AVAudioApplication.requestRecordPermission { mic in
                SFSpeechRecognizer.requestAuthorization { st in
                    completion(mic && st == .authorized)
                }
            }
        } else {
            AVAudioSession.sharedInstance().requestRecordPermission { mic in
                SFSpeechRecognizer.requestAuthorization { st in
                    completion(mic && st == .authorized)
                }
            }
        }
    }

    // 録音開始（入力＋出力を維持）
    func start() throws {
        stop()              // 前回を完全停止（tap除去まで）
        engine.reset()      // グラフ初期化

        // UI クリア & バッファ初期化
        sendUnity(UnityCallback.gameObject, UnityCallback.onSpeech, "")
        currentTranscript = ""
        lastFinalText = nil

        let session = AVAudioSession.sharedInstance()

        // ★ 出力も生かす
        try session.setCategory(.playAndRecord,
                                mode: .measurement,
                                options: [.defaultToSpeaker, .allowBluetooth])

        // 実機に合わせやすい希望値
        try? session.setPreferredSampleRate(48_000)
        try session.setPreferredIOBufferDuration(0.01)

        // Active 化
        try session.setActive(true, options: [])

        // 入力ノードの実フォーマットを取得して tap に渡す
        let input = engine.inputNode
        input.removeTap(onBus: 0)
        var tapFormat = input.inputFormat(forBus: 0)

        // まれに 0Hz 対策のリトライ
        if tapFormat.sampleRate == 0 {
            try? session.setActive(false, options: .notifyOthersOnDeactivation)
            try? session.setPreferredSampleRate(48_000)
            try session.setActive(true, options: [])
            tapFormat = input.inputFormat(forBus: 0)
        }
        guard tapFormat.sampleRate > 0 else {
            sendUnity(UnityCallback.gameObject, "OnSpeechError", "input format invalid (0 Hz)")
            throw NSError(domain: "Speech", code: -101, userInfo: [NSLocalizedDescriptionKey:"0Hz input format"])
        }

        // リクエスト作成
        request = SFSpeechAudioBufferRecognitionRequest()
        request?.shouldReportPartialResults = true

        // ★ フォーマットを明示指定
        input.installTap(onBus: 0, bufferSize: 1024, format: tapFormat) { [weak self] buf, _ in
            self?.request?.append(buf)
        }

        engine.prepare()
        try engine.start()

        BridgeLog.d("Audio", "REC start sr=\(tapFormat.sampleRate), ch=\(tapFormat.channelCount) / sess sr=\(session.sampleRate), ioBuf=\(session.ioBufferDuration)")

        guard let recognizer = recognizer,
              let req = request,
              recognizer.isAvailable else {
            throw NSError(domain: "Speech", code: -100, userInfo: nil)
        }

        task = recognizer.recognitionTask(with: req) { [weak self] result, err in
            guard let self else { return }
            if let text = result?.bestTranscription.formattedString {
                self.currentTranscript = text
                sendUnity(UnityCallback.gameObject, UnityCallback.onSpeech, text)
            }
            if err != nil || (result?.isFinal ?? false) {
                if let final = result?.bestTranscription.formattedString, !final.isEmpty {
                    self.lastFinalText = final
                }
                self.stop()
            }
        }

        isRunning = true
    }

    // 録音停止
    func stop() {
        engine.stop()
        engine.inputNode.removeTap(onBus: 0)

        // ★ バッファ供給の終了を明示
        request?.endAudio()

        task?.cancel(); task = nil
        request = nil
        isRunning = false
        engine.reset()

        // ★ セッションは落とさない（出力を維持して Unity を安定化）
        // try? AVAudioSession.sharedInstance().setActive(false, options: .notifyOthersOnDeactivation)
    }

    // 録音の一時停止（未使用）
    func pause() {
        guard isRunning else { return }
        engine.pause()
        engine.inputNode.removeTap(onBus: 0)
        isRunning = false
    }
    func resume() { }
}

// =======================
// Text to Speech（JIT送出）
// =======================
final class TTSManager {
    static let synth = AVSpeechSynthesizer()

    @discardableResult
    static func stopSpeaking() -> Bool {
        var stopped = false
        if synth.isSpeaking { stopped = synth.stopSpeaking(at: .immediate) }
        return stopped
    }

    static func speak(_ text: String,
                      lang: String = "ja-JP",
                      rate: Float = 0.5,
                      pitch: Float = 1.0)
    {
        DispatchQueue.main.async {
            _ = stopSpeaking()

            // 録音は止める（tap を外す）
            SpeechManager.shared.stop()

            let session = AVAudioSession.sharedInstance()
            do {
                // 出力・入力を維持（切替の揺れ最小化）
                try session.setCategory(.playAndRecord,
                                        mode: .spokenAudio,
                                        options: [.defaultToSpeaker, .duckOthers, .allowBluetooth])
                try? session.setPreferredSampleRate(48_000)
                try? session.setPreferredIOBufferDuration(0.01)
                try session.setActive(true)
                try? session.overrideOutputAudioPort(.speaker)
            } catch {
                print("TTS session error:", error)
            }

            let u = AVSpeechUtterance(string: text)
            u.voice = AVSpeechSynthesisVoice(language: lang)
            u.rate  = rate
            u.pitchMultiplier = pitch

            synth.delegate = TTSManagerDelegate.shared
            TTSManagerDelegate.shared.resetForNewUtterance(rate: rate)

            // ★ そのまま喋る。willSpeakRange から逐次で Unity に送る
            synth.speak(u)
        }
    }
}

// willSpeakRange から1文字ずつ即時送出（将来分は送らない）
final class TTSManagerDelegate: NSObject, AVSpeechSynthesizerDelegate {
    static let shared = TTSManagerDelegate()

    private var lastRate: Float = 0.5

    func resetForNewUtterance(rate: Float) {
        lastRate = rate
    }

    private func baseDur(rate: Float) -> Double {
        // おおよそ以前の設計と同等の体感（0.12s中心）
        let r = max(0.3, min(0.7, Double(rate)))   // 0.3〜0.7
        return max(0.08, 0.22 - (r * 0.14))        // 0.16〜0.12s 程度
    }

    private func vowelOfKana(_ s: String) -> [String] {
        // ひらがなベース。カタカナはひらがな化して判定。
        let hira = (s as NSString).applyingTransform(.hiraganaToKatakana, reverse: true) ?? s
        switch hira {
        case "あ","か","さ","た","な","は","ま","や","ら","わ","が","ざ","だ","ば","ぱ","ぁ","ゃ","ー": return ["a"]
        case "い","き","し","ち","に","ひ","み","り","ぎ","じ","ぢ","び","ぴ","ぃ": return ["i"]
        case "う","く","す","つ","ぬ","ふ","む","ゆ","る","ぐ","ず","づ","ぶ","ぷ","ぅ","ゅ": return ["u"]
        case "え","け","せ","て","ね","へ","め","れ","げ","ぜ","で","べ","ぺ","ぇ": return ["e"]
        case "お","こ","そ","と","の","ほ","も","よ","ろ","ご","ぞ","ど","ぼ","ぽ","ぉ","ょ": return ["o"]
        default: return [] // 漢字・記号などは無母音
        }
    }

    // ---- AVSpeechSynthesizerDelegate ----
    func speechSynthesizer(_ s: AVSpeechSynthesizer, didStart u: AVSpeechUtterance) {
        BridgeLog.d("LipRT", "didStart")
    }

    func speechSynthesizer(_ s: AVSpeechSynthesizer, didFinish u: AVSpeechUtterance) {
        BridgeLog.d("LipRT", "didFinish")
    }

    func speechSynthesizer(_ s: AVSpeechSynthesizer,
                           willSpeakRangeOfSpeechString range: NSRange,
                           utterance u: AVSpeechUtterance)
    {
        // willSpeak が来た「いま読もうとしている文字レンジ」だけを即時送出
        let full = u.speechString as NSString
        let chunk = full.substring(with: range)

        // 1文字ごとに lip 情報へ
        let base = baseDur(rate: lastRate)
        for ch in chunk {
            let ss = String(ch)
            let vowels = vowelOfKana(ss)
            let dur = vowels.isEmpty ? max(0.06, base * 0.6) : base

            let payload: [String: Any] = ["text": ss, "dur": dur, "vowels": vowels]
            if let data = try? JSONSerialization.data(withJSONObject: payload),
               let json = String(data: data, encoding: .utf8) {
                sendUnity(UnityCallback.gameObject, "OnTTSRange", json)
                // ここでは先行送出しない（=JIT）。Unity側は届いた順に消化。
            }
        }
    }
}

// =======================
// ChatGPT Web（API直叩き版）
// =======================
final class ChatGPTWeb: NSObject {

    static let shared = ChatGPTWeb()

    private let apiURL = URL(string: "https://api.openai.com/v1/chat/completions")!
    private let modelName = "gpt-4o-mini"
    private let systemPrompt = "ユーザの発言への口頭での返答内容を出力してください。なお、口頭なのであまり長い文章にはしないでください。"

    private var apiKey: String? = Bundle.main.object(forInfoDictionaryKey: "OPENAI_API_KEY") as? String

    private var currentTask: URLSessionDataTask?
    private var latestRequestId: String?

    func ensureReady(presentIfNeeded: Bool, completion: @escaping (Bool)->Void) {
        let ok = (apiKey?.isEmpty == false)
        if !ok { BridgeLog.e("API", "OPENAI_API_KEY が Info.plist にありません。") }
        completion(ok)
    }

    func ask(_ unityText: String) {
        // 送信開始時に録音を止める
        SpeechManager.shared.stop()

        // ★ 送信「開始時」に UI と内部バッファをクリア
        sendUnity(UnityCallback.gameObject, UnityCallback.onSpeech, "")
        SpeechManager.shared.clearBufferForNextUtterance()

        let speechFinal = SpeechManager.shared.takeFinalTranscript()
        let text = (speechFinal?.isEmpty == false) ? speechFinal! : unityText

        BridgeLog.d("Ask", "len=\(text.count) (from=\(speechFinal == nil ? "unity" : "speech-final"))")

        ensureReady(presentIfNeeded: false) { ok in
            guard ok, let key = self.apiKey else {
                let msg = "（未設定: OPENAI_API_KEY がありません）"
                sendUnity(UnityCallback.gameObject, UnityCallback.onReply, msg)
                return
            }

            // 旧タスク破棄 & リクエストID更新
            self.currentTask?.cancel()
            let reqId = UUID().uuidString
            self.latestRequestId = reqId

            let messages: [[String: String]] = [
                ["role":"system", "content": self.systemPrompt],
                ["role":"user",   "content": text]
            ]

            var req = URLRequest(url: self.apiURL)
            req.httpMethod = "POST"
            req.setValue("Bearer \(key)", forHTTPHeaderField: "Authorization")
            req.setValue("application/json", forHTTPHeaderField: "Content-Type")
            req.httpBody = try? JSONSerialization.data(withJSONObject: [
                "model": self.modelName,
                "messages": messages,
                "temperature": 0.7
            ])

            let task = URLSession.shared.dataTask(with: req) { data, resp, error in
                guard reqId == self.latestRequestId else { return }
                if let error, (error as NSError).code != NSURLErrorCancelled {
                    DispatchQueue.main.async {
                        sendUnity(UnityCallback.gameObject, UnityCallback.onReply, "（通信エラー）\(error.localizedDescription)")
                    }
                    return
                }
                guard let http = resp as? HTTPURLResponse,
                      (200..<300).contains(http.statusCode),
                      let data = data else {
                    DispatchQueue.main.async {
                        let raw = String(data: data ?? Data(), encoding: .utf8) ?? ""
                        sendUnity(UnityCallback.gameObject, UnityCallback.onReply, "（HTTPエラー）\(raw)")
                    }
                    return
                }
                do {
                    if let json = try JSONSerialization.jsonObject(with: data) as? [String: Any],
                       let choices = json["choices"] as? [[String: Any]],
                       let message = choices.first?["message"] as? [String: Any],
                       let reply = message["content"] as? String {
                        DispatchQueue.main.async {
                            if reqId == self.latestRequestId {
                                sendUnity(UnityCallback.gameObject, UnityCallback.onReply, reply)
                                TTSManager.speak(reply)
                            }
                        }
                    } else {
                        DispatchQueue.main.async {
                            sendUnity(UnityCallback.gameObject, UnityCallback.onReply, "（応答解析に失敗しました）")
                        }
                    }
                } catch {
                    DispatchQueue.main.async {
                        sendUnity(UnityCallback.gameObject, UnityCallback.onReply, "（JSONエラー）\(error.localizedDescription)")
                    }
                }
            }
            self.currentTask = task
            task.resume()
        }
    }
}

// =======================
// Unity から呼ばれる関数
// =======================
@_cdecl("ChatGPTBridge_SetupSpeech")
public func ChatGPTBridge_SetupSpeech(_ goPtr: UnsafePointer<CChar>, _ methodPtr: UnsafePointer<CChar>) {
    UnityCallback.gameObject = String(cString: goPtr)
    UnityCallback.onSpeech   = String(cString: methodPtr)
}

@_cdecl("ChatGPTBridge_RequestPermissions")
public func ChatGPTBridge_RequestPermissions() {
    SpeechManager.shared.requestPermissions { ok in
        let msg = ok ? "granted" : "denied"
        sendUnity(UnityCallback.gameObject, "OnSpeechPermission", msg)
    }
}

@_cdecl("ChatGPTBridge_StartSpeech")
public func ChatGPTBridge_StartSpeech() {
    DispatchQueue.main.async {
        // 新規録音前に読み上げを止める
        TTSManager.stopSpeaking()
        do { try SpeechManager.shared.start() }
        catch {
            sendUnity(UnityCallback.gameObject, "OnSpeechError", "start error: \(error.localizedDescription)")
        }
    }
}

@_cdecl("ChatGPTBridge_StopSpeech")
public func ChatGPTBridge_StopSpeech() {
    DispatchQueue.main.async { SpeechManager.shared.stop() }
}

@_cdecl("ChatGPTBridge_Speak")
public func ChatGPTBridge_Speak(_ textPtr: UnsafePointer<CChar>) {
    let text = String(cString: textPtr)
    DispatchQueue.main.async { TTSManager.speak(text) }
}

@_cdecl("ChatGPTBridge_SetupChatGPT")
public func ChatGPTBridge_SetupChatGPT(_ goPtr: UnsafePointer<CChar>, _ methodPtr: UnsafePointer<CChar>) {
    UnityCallback.gameObject = String(cString: goPtr)
    UnityCallback.onReply    = String(cString: methodPtr)
    ChatGPTWeb.shared.ensureReady(presentIfNeeded: false) { _ in }
}

@_cdecl("ChatGPTBridge_Ask")
public func ChatGPTBridge_Ask(_ textPtr: UnsafePointer<CChar>) {
    // 新規送信前に読み上げを止める
    TTSManager.stopSpeaking()
    let question = String(cString: textPtr)
    ChatGPTWeb.shared.ask(question)
}