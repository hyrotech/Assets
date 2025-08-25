import Foundation
import UIKit
import AVFoundation
import Speech
import AVFAudio
import AudioToolbox   // ★ 追加

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

    // PCM ストリーミング（Unity 側の受け口）
    static let onPCMBegin  = "OnTTSStreamBegin"
    static let onPCMChunk  = "OnTTSStreamChunk"
    static let onPCMEnd    = "OnTTSStreamEnd"
}

// =======================
// Speech to Text（現状そのまま）
// =======================
final class SpeechManager {
    static let shared = SpeechManager()

    private let engine = AVAudioEngine()
    private var request: SFSpeechAudioBufferRecognitionRequest?
    private var task: SFSpeechRecognitionTask?
    private let recognizer = SFSpeechRecognizer(locale: Locale(identifier: "ja-JP"))

    private(set) var isRunning = false

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

    func start() throws {
        stop()
        engine.reset()

        sendUnity(UnityCallback.gameObject, UnityCallback.onSpeech, "")
        currentTranscript = ""
        lastFinalText = nil

        let session = AVAudioSession.sharedInstance()
        try session.setCategory(.playAndRecord, mode: .measurement, options: [.defaultToSpeaker, .allowBluetooth])
        try? session.setPreferredSampleRate(48_000)
        try? session.setPreferredInputNumberOfChannels(1)
        try session.setPreferredIOBufferDuration(0.01)
        try session.setActive(true, options: [])

        let input = engine.inputNode
        input.removeTap(onBus: 0)
        var tapFormat = input.inputFormat(forBus: 0)

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

        request = SFSpeechAudioBufferRecognitionRequest()
        request?.shouldReportPartialResults = true

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

    func stop() {
        engine.stop()
        engine.inputNode.removeTap(onBus: 0)
        request?.endAudio()
        task?.cancel(); task = nil
        request = nil
        isRunning = false
        engine.reset()
    }
}

// =======================
// TTS → PCM（iOSでは再生しないでPCMのみ送る）
// =======================
final class TTSManager {
    static let synth = AVSpeechSynthesizer()

    private static var currentStreamId: String?
    private static var sentAnyChunk: Bool = false

    @discardableResult
    static func stopSpeaking() -> Bool {
        var stopped = false
        if synth.isSpeaking { stopped = synth.stopSpeaking(at: .immediate) }
        currentStreamId = nil
        sentAnyChunk = false
        return stopped
    }

    static func speak(_ text: String,
                      lang: String = "ja-JP",
                      rate: Float = 0.5,
                      pitch: Float = 1.0)
    {
        DispatchQueue.global(qos: .userInitiated).async {
            _ = stopSpeaking()

            // iOS 側では出力しない（ただしセッションは有効化）
            let session = AVAudioSession.sharedInstance()
            do {
                try session.setCategory(.playback, mode: .default, options: [.mixWithOthers])
                try session.setActive(true)
            } catch {
                BridgeLog.e("TTS", "session error \(error.localizedDescription)")
            }

            let u = AVSpeechUtterance(string: text)
            u.voice = AVSpeechSynthesisVoice(language: lang)
            u.rate  = rate
            u.pitchMultiplier = pitch

            let streamId = UUID().uuidString
            currentStreamId = streamId
            sentAnyChunk = false

            var sentBegin = false
            var sr: Double = 0
            var ch: AVAudioChannelCount = 0

            Self.synth.write(u) { (buffer: AVAudioBuffer) in
                // 他の発話に切り替わっていたら破棄
                guard currentStreamId == streamId else { return }

                // 完了マーカー（frameLength == 0）
                if let pcm = buffer as? AVAudioPCMBuffer, pcm.frameLength == 0 {
                    DispatchQueue.main.async {
                        let endPayload: [String: Any] = ["id": streamId]
                        let data = try? JSONSerialization.data(withJSONObject: endPayload)
                        let msg = String(data: data ?? Data(), encoding: .utf8) ?? "{}"
                        sendUnity(UnityCallback.gameObject, UnityCallback.onPCMEnd, msg)
                    }
                    return
                }

                guard let pcmIn = buffer as? AVAudioPCMBuffer else { return }
                let fmtIn = pcmIn.format
                sr = fmtIn.sampleRate
                ch = fmtIn.channelCount

                // ---- 目標: Float32 Interleaved ----
                var targetDesc = AudioStreamBasicDescription(
                    mSampleRate: sr,
                    mFormatID: kAudioFormatLinearPCM,
                    mFormatFlags: kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked, // ← interleaved
                    mBytesPerPacket: 4 * ch,
                    mFramesPerPacket: 1,
                    mBytesPerFrame: 4 * ch,
                    mChannelsPerFrame: ch,
                    mBitsPerChannel: 32,
                    mReserved: 0
                )
                guard let fmtOut = AVAudioFormat(streamDescription: &targetDesc) else { return }

                let needsConvert = !(fmtIn.streamDescription.pointee.mFormatID == fmtOut.streamDescription.pointee.mFormatID
                                     && fmtIn.channelCount == fmtOut.channelCount
                                     && fmtIn.sampleRate == fmtOut.sampleRate
                                     && fmtIn.isInterleaved)

                var pcmOut: AVAudioPCMBuffer = pcmIn

                if needsConvert {
                    if let conv = AVAudioConverter(from: fmtIn, to: fmtOut) {
                        let dstCap = AVAudioFrameCount(pcmIn.frameLength)
                        guard let dst = AVAudioPCMBuffer(pcmFormat: fmtOut, frameCapacity: dstCap) else { return }
                        var err: NSError?
                        let inputBlock: AVAudioConverterInputBlock = { _, outStatus in
                            outStatus.pointee = .haveData
                            return pcmIn
                        }
                        _ = conv.convert(to: dst, error: &err, withInputFrom: inputBlock)
                        if let err { BridgeLog.e("PCM", "convert error \(err.localizedDescription)"); return }
                        pcmOut = dst
                    } else {
                        // コンバータが作れない場合は諦めてそのまま（後段で非インターリーブも扱う）
                        pcmOut = pcmIn
                    }
                }

                let frames = Int(pcmOut.frameLength)
                if frames == 0 { return }

                // Begin（最初のチャンクの前に一度）
                if !sentBegin {
                    sentBegin = true
                    DispatchQueue.main.async {
                        let payload: [String: Any] = [
                            "id": streamId,
                            "sr": sr,
                            "ch": Int(ch),
                            "fmt": "f32",
                            "ilv": true
                        ]
                        let data = try? JSONSerialization.data(withJSONObject: payload)
                        let msg = String(data: data ?? Data(), encoding: .utf8) ?? "{}"
                        sendUnity(UnityCallback.gameObject, UnityCallback.onPCMBegin, msg)
                    }
                }

                // ---- 生バイトの取り出し ----
                // interleaved なら audioBufferList から mData をそのまま送る
                let abl = pcmOut.audioBufferList.pointee.mBuffers
                guard let mData = abl.mData, abl.mDataByteSize > 0 else { return }
                let byteCount = Int(abl.mDataByteSize)

                let data = Data(bytes: mData, count: byteCount)
                let b64  = data.base64EncodedString()

                DispatchQueue.main.async {
                    let payload: [String: Any] = ["id": streamId, "b64": b64, "frames": frames]
                    let data = try? JSONSerialization.data(withJSONObject: payload)
                    let msg = String(data: data ?? Data(), encoding: .utf8) ?? "{}"
                    sendUnity(UnityCallback.gameObject, UnityCallback.onPCMChunk, msg)
                }
                sentAnyChunk = true
            }
        }
    }
}

// === 置き換え: TTSManagerDelegate（不要なら空実装でOK）===
final class TTSManagerDelegate: NSObject, AVSpeechSynthesizerDelegate {
    static let shared = TTSManagerDelegate()
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

        // 送信「開始時」に UI と内部バッファをクリア
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
    TTSManager.stopSpeaking()
    let question = String(cString: textPtr)
    ChatGPTWeb.shared.ask(question)
}