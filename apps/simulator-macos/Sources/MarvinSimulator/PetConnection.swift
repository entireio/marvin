import AppKit
import Foundation
import Security
import PetDeviceCore

struct PetBinding: Codable {
    var serverUrl: String, deviceId: String, credential: String
    var credentialExpiresAt: Double
}

private final class PetRedirectBlocker: NSObject, URLSessionTaskDelegate {
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) { completionHandler(nil) }
}

enum PetCredentialStore {
    private static var query: [String: Any] { [kSecClass as String:kSecClassGenericPassword,kSecAttrService as String:"io.entire.marvin.simulator",kSecAttrAccount as String:"pet-binding"] }
    static func load() throws -> PetBinding? {
        var q=query;q[kSecReturnData as String]=true;q[kSecMatchLimit as String]=kSecMatchLimitOne
        var result: CFTypeRef?;let status=SecItemCopyMatching(q as CFDictionary,&result)
        if status==errSecItemNotFound { return nil }
        guard status==errSecSuccess,let data=result as? Data else { throw NSError(domain:NSOSStatusErrorDomain,code:Int(status)) }
        return try JSONDecoder().decode(PetBinding.self,from:data)
    }
    static func save(_ binding: PetBinding) throws {
        let data=try JSONEncoder().encode(binding)
        let status=SecItemUpdate(query as CFDictionary,[kSecValueData as String:data] as CFDictionary)
        if status==errSecItemNotFound {
            var q=query;q[kSecValueData as String]=data
            let added=SecItemAdd(q as CFDictionary,nil)
            guard added==errSecSuccess else { throw NSError(domain:NSOSStatusErrorDomain,code:Int(added)) };return
        }
        guard status==errSecSuccess else { throw NSError(domain:NSOSStatusErrorDomain,code:Int(status)) }
    }
    static func clear() throws {
        let status=SecItemDelete(query as CFDictionary)
        guard status==errSecSuccess || status==errSecItemNotFound else { throw NSError(domain:NSOSStatusErrorDomain,code:Int(status)) }
    }
}

/// Main-thread state owner. Network awaits do not block rendering; every stale
/// callback is fenced by a connection generation. No local server or bridge.
final class PetConnection {
    var onChange: (() -> Void)?, onConnected: (() -> Void)?, onStop: (() -> Void)?
    private(set) var status = "Not connected", online=false
    private(set) var binding: PetBinding?
    private(set) var pairing=false
    private(set) var credentialNotice: String?
    let state: PetDeviceState
    private var socket: URLSessionWebSocketTask?, receiveTask: Task<Void,Never>?, sendTask: Task<Void,Never>?
    private var reconnect: Timer?, handshake: Timer?
    private var generation=0, seq=0, epoch=0, backoff=1.0, clockOffset=0.0
    private let bootId=UUID().uuidString.lowercased()
    private let preferencesURL: URL
    private var transient=false
    private let redirectBlocker=PetRedirectBlocker()
    private lazy var session: URLSession = {
        let config=URLSessionConfiguration.ephemeral
        config.httpCookieStorage=nil;config.urlCache=nil;config.timeoutIntervalForRequest=15
        return URLSession(configuration:config,delegate:redirectBlocker,delegateQueue:nil)
    }()
    init(testDirectory: URL? = nil) throws {
        transient=testDirectory != nil
        let folder=testDirectory ?? FileManager.default.urls(for:.applicationSupportDirectory,in:.userDomainMask)[0].appendingPathComponent("MarvinSimulator/Pet",isDirectory:true)
        preferencesURL=folder.appendingPathComponent("preferences.json")
        state=try PetDeviceState(ledgerURL:folder.appendingPathComponent("commands.json"))
        if FileManager.default.fileExists(atPath:preferencesURL.path) { state.preferences=try JSONDecoder().decode(PetPreferences.self,from:Data(contentsOf:preferencesURL)) }
        state.onStatus={ [weak self] id,status,code in
            var event: [String:Any]=["type":"command_status","id":id,"status":status]
            if let code { event["code"]=code };self?.send(event)
        }
    }
    static func origin(_ text: String) throws -> URL {
        guard let parts=URLComponents(string:text.trimmingCharacters(in:.whitespacesAndNewlines)),
              let host=parts.host,parts.user==nil,parts.password==nil,parts.query==nil,parts.fragment==nil,
              parts.path.isEmpty || parts.path=="/",
              parts.scheme=="https" || (parts.scheme=="http" && ["127.0.0.1","localhost","::1","[::1]"].contains(host)),
              let url=parts.url else { throw NSError(domain:"MarvinPet",code:1,userInfo:[NSLocalizedDescriptionKey:"Enter an HTTPS server address, or HTTP on localhost for development."]) }
        return url
    }
    func restore() {
        do { binding=try PetCredentialStore.load();if binding != nil { connect() } }
        catch { status="Could not read the saved connection from Keychain.";onChange?() }
    }
    func useTestBinding(_ value: PetBinding) { transient=true;binding=value;connect() }
    func pair(server: String, code: String) {
        guard !pairing else { return }
        guard binding==nil else { status="Unlink the current simulator in the web app before pairing again.";onChange?();return }
        do {
            let url=try Self.origin(server)
            guard code.range(of:"^[A-Za-z0-9_-]{43}$",options:.regularExpression) != nil else { throw NSError(domain:"MarvinPet",code:2,userInfo:[NSLocalizedDescriptionKey:"Enter the one-time simulator code from the web app."]) }
            pairing=true;generation+=1;let attempt=generation;status="Pairing…";onChange?()
            Task { @MainActor [weak self] in
                guard let self else { return }
                defer { self.pairing=false;self.onChange?() }
                do {
                    var request=URLRequest(url:url.appendingPathComponent("api/device/simulator/pair"));request.httpMethod="POST"
                    request.setValue("application/json",forHTTPHeaderField:"Content-Type");request.httpBody=try JSONSerialization.data(withJSONObject:["code":code])
                    let (data,response)=try await self.session.data(for:request)
                    guard self.generation==attempt else { return }
                    guard let http=response as? HTTPURLResponse,http.statusCode==200 else {
                        let reply=(try? JSONSerialization.jsonObject(with:data)) as? [String:Any]
                        throw NSError(domain:"MarvinPet",code:3,userInfo:[NSLocalizedDescriptionKey:(reply?["error"] as? [String:Any])?["message"] as? String ?? "Pairing was refused. Create a new code in Marvin."])
                    }
                    struct Reply: Decodable { let deviceId: String,credential: String,credentialExpiresAt: Double }
                    let value=try JSONDecoder().decode(Reply.self,from:data)
                    let bound=PetBinding(serverUrl:url.absoluteString,deviceId:value.deviceId,credential:value.credential,credentialExpiresAt:value.credentialExpiresAt)
                    // Retain a successful binding even if Keychain is unavailable:
                    // the code was consumed, so losing the receipt strands pairing.
                    self.binding=bound
                    do { if !self.transient { try PetCredentialStore.save(bound) } } catch { self.credentialNotice="Paired for this session; Keychain could not save reconnect credentials.";self.onChange?() }
                    self.pairing=false;self.connect()
                } catch { if self.generation==attempt { self.status=error.localizedDescription;self.onChange?() } }
            }
        } catch { status=error.localizedDescription;onChange?() }
    }
    func connect() {
        guard let binding else { return }
        disconnect()
        guard binding.credentialExpiresAt>Date().timeIntervalSince1970*1000 else { status="Connection expired. Unlink this simulator in Marvin and pair again.";onChange?();return }
        do {
            let origin=try Self.origin(binding.serverUrl);var parts=URLComponents(url:origin,resolvingAgainstBaseURL:false)!
            parts.scheme=parts.scheme=="https" ? "wss":"ws";parts.path="/api/device/socket"
            var request=URLRequest(url:parts.url!);request.setValue("Bearer \(binding.credential)",forHTTPHeaderField:"Authorization")
            let ws=session.webSocketTask(with:request);ws.maximumMessageSize=16384;socket=ws;seq=0
            let attempt=generation;status="Connecting…";onChange?();ws.resume()
            handshake=Timer.scheduledTimer(withTimeInterval:6,repeats:false) { [weak self] _ in self?.failed(attempt:attempt) }
            receiveTask=Task { @MainActor [weak self] in
                guard let self else { return }
                do {
                    let hello: [String:Any]=["type":"hello","protocol":["major":1,"minor":13],"deviceId":binding.deviceId,"bootId":self.bootId,"firmware":"swift-simulator-1","capabilities":PetDeviceState.capabilities,
                        "headCalibration":try self.object(self.state.preferences.headCalibration),"eyeSettings":["design":self.state.preferences.eyeDesign],"displaySettings":["showBatteryIcon":self.state.preferences.showBatteryIcon],"batteryStatus":["levelPercent":NSNull(),"voltageMv":NSNull(),"charging":NSNull()]]
                    try await ws.send(.string(String(decoding:try JSONSerialization.data(withJSONObject:hello),as:UTF8.self)))
                    while !Task.isCancelled,self.generation==attempt {
                        let message=try await ws.receive()
                        guard self.generation==attempt else { return }
                        guard case .string(let text)=message else { throw CocoaError(.coderInvalidValue) }
                        try self.receive(Data(text.utf8))
                    }
                } catch { self.failed(attempt:attempt) }
            }
        } catch { status=error.localizedDescription;onChange?() }
    }
    private func receive(_ data: Data) throws {
        guard let event=try JSONSerialization.jsonObject(with:data) as? [String:Any],let type=event["type"] as? String else { throw CocoaError(.coderInvalidValue) }
        switch type {
        case "welcome":
            guard let value=event["epoch"] as? Int,value>0,let serverTime=event["serverTime"] as? Double else { throw CocoaError(.coderInvalidValue) }
            epoch=value;clockOffset=serverTime-Date().timeIntervalSince1970*1000;online=true;backoff=1;handshake?.invalidate();status="Simulator online";onConnected?()
        case "ping":
            send(["type":"heartbeat","seq":seq]);seq+=1
        case "command":
            guard online,let binding else { throw CocoaError(.coderInvalidValue) }
            state.receive(try JSONDecoder().decode(PetCommand.self,from:data),deviceId:binding.deviceId,epoch:epoch,bootId:bootId,wallMs:Date().timeIntervalSince1970*1000+clockOffset,now:ProcessInfo.processInfo.systemUptime)
        case "cancel": if let id=event["id"] as? String { state.cancel(id) }
        case "unlink":
            guard event["deviceId"] as? String==binding?.deviceId,event["epoch"] as? Int==epoch else { return }
            send(["type":"unlink_ack","deviceId":binding!.deviceId,"epoch":epoch])
            let sent=sendTask
            Task { @MainActor [weak self] in await sent?.value;self?.forget() }
        case "head_calibration","head_calibration_preview":
            let calibration=try JSONDecoder().decode(PetCalibration.self,from:data)
            guard calibration.valid else { throw CocoaError(.coderInvalidValue) }
            if type=="head_calibration" { state.preferences.headCalibration=calibration;try savePreferences();send(["type":type,"yawCenter":calibration.yawCenter,"pitchCenter":calibration.pitchCenter,"yawReversed":calibration.yawReversed,"pitchReversed":calibration.pitchReversed]) }
            // Preview is applied in the same rendering path and expires locally.
            previewCalibration=calibration;previewUntil=ProcessInfo.processInfo.systemUptime+2
        case "eye_settings":
            guard let design=event["design"] as? String,["classic","solid","friendly"].contains(design) else { throw CocoaError(.coderInvalidValue) }
            state.preferences.eyeDesign=design;try savePreferences();send(["type":type,"design":design])
        case "display_settings":
            guard let show=event["showBatteryIcon"] as? Bool else { throw CocoaError(.coderInvalidValue) }
            state.preferences.showBatteryIcon=show;try savePreferences();send(["type":type,"showBatteryIcon":show])
        case "error": disconnect();status="Device access was refused. Unlink the simulator in Marvin and pair again.";onChange?();return
        default: throw CocoaError(.coderInvalidValue)
        }
        onChange?()
    }
    private var previewCalibration: PetCalibration?, previewUntil=0.0
    var calibration: PetCalibration { ProcessInfo.processInfo.systemUptime<previewUntil ? previewCalibration ?? state.preferences.headCalibration:state.preferences.headCalibration }
    private func object<T: Encodable>(_ value:T) throws -> Any { try JSONSerialization.jsonObject(with:JSONEncoder().encode(value)) }
    private func savePreferences() throws {
        try FileManager.default.createDirectory(at:preferencesURL.deletingLastPathComponent(),withIntermediateDirectories:true)
        try JSONEncoder().encode(state.preferences).write(to:preferencesURL,options:.atomic)
    }
    func send(_ event: [String:Any]) {
        guard let ws=socket,let bytes=try? JSONSerialization.data(withJSONObject:event) else { return }
        let previous=sendTask,attempt=generation
        sendTask=Task { @MainActor [weak self] in
            await previous?.value
            guard let self,self.generation==attempt else { return }
            do { try await ws.send(.string(String(decoding:bytes,as:UTF8.self))) } catch { self.failed(attempt:attempt) }
        }
    }
    private func failed(attempt: Int) {
        guard attempt==generation,binding != nil else { return }
        let rejected=socket?.closeCode.rawValue==4401
        disconnect()
        if rejected { status="Device access expired or was revoked. Unlink the simulator in Marvin and pair again.";onChange?();return }
        status="Reconnecting…";onChange?()
        let delay=backoff;backoff=min(15,backoff*2)
        reconnect=Timer.scheduledTimer(withTimeInterval:delay,repeats:false) { [weak self] _ in self?.connect() }
    }
    func disconnect() {
        guard !pairing else { return }
        generation+=1;reconnect?.invalidate();handshake?.invalidate();receiveTask?.cancel();sendTask?.cancel()
        socket?.cancel(with:.normalClosure,reason:nil);socket=nil;online=false;state.stop();onStop?()
        status=binding==nil ? "Not connected":"Disconnected";onChange?()
    }
    func forget() {
        disconnect()
        do { if !transient { try PetCredentialStore.clear() };binding=nil;status="Unlinked. Create a new simulator code in Marvin." }
        catch { status="Unlinked, but Keychain cleanup failed. Remove the saved Marvin simulator entry in Keychain Access." }
        onChange?()
    }
}
