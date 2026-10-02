import AppKit
import CryptoKit
import UniformTypeIdentifiers
import Darwin

// JSON is private IPC from the signed parent. stdout contains protocol messages only.
private let outputLock = NSLock()
private func emit(_ event: String, id: String? = nil, error: String? = nil, paths: [String]? = nil, operation: UInt? = nil,
                  window: Int? = nil, x: Double? = nil, y: Double? = nil, detail: String? = nil) {
    var value: [String: Any] = ["event": event]
    if let id { value["id"] = id }
    if let error { value["error"] = error }
    if let paths { value["paths"] = paths }
    if let operation { value["operation"] = operation }
    if let window { value["window"] = window }
    if let x { value["x"] = x }
    if let y { value["y"] = y }
    if let detail { value["detail"] = detail }
    guard let data = try? JSONSerialization.data(withJSONObject: value, options: [.sortedKeys]) else { return }
    outputLock.lock(); defer { outputLock.unlock() }
    FileHandle.standardOutput.write(data)
    FileHandle.standardOutput.write(Data([10]))
}

private struct Root: Decodable {
    let name: String
    let isDirectory: Bool
}
private struct Command: Decodable {
    let command: String
    let id: String
    let stagingPath: String?
    let roots: [Root]?
    let error: String?
}
private func failure(_ text: String) -> NSError {
    NSError(domain: "com.linkbridge.filedrag", code: 1, userInfo: [NSLocalizedDescriptionKey: text])
}

private final class DragSourceView: NSView {
    weak var drag: Drag?
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func mouseDown(with event: NSEvent) {
        do { try drag?.startNative(with: event) }
        catch { drag?.cancel(error.localizedDescription) }
    }
}

private final class Drag: NSObject, NSDraggingSource, NSFilePromiseProviderDelegate {
    private struct OwnedTarget { let url: URL; let device: dev_t; let inode: ino_t }
    let id: String
    let roots: [Root]
    let staging: URL
    let condition = NSCondition()
    let writeQueue = OperationQueue()
    let sourceView = DragSourceView(frame: NSRect(x: 0, y: 0, width: 120, height: 90))
    var window: NSWindow?
    var session: NSDraggingSession?
    var providers: [NSFilePromiseProvider] = []
    var ready = false
    var cancelled: Error?
    var ended = false
    var accepted = false
    var pending = 0
    var written = 0
    var destinations: [String] = []
    private var ownedTargets: [OwnedTarget] = []
    var failed = false
    var finished = false
    var onFinish: (() -> Void)?

    init(command: Command) throws {
        guard UUID(uuidString: command.id) != nil, let roots = command.roots,
              !roots.isEmpty, roots.count <= 256, let path = command.stagingPath,
              path.hasPrefix("/") else { throw failure("无效文件拖放请求") }
        var names = Set<String>()
        for root in roots {
            guard !root.name.isEmpty, root.name != ".", root.name != "..",
                  !root.name.contains("/"), !root.name.contains("\\"), !root.name.contains("\0"),
                  names.insert(root.name.folding(options: [.caseInsensitive], locale: Locale(identifier: "en_US_POSIX"))).inserted
            else { throw failure("文件名称无效或重复") }
        }
        let rawStaging = URL(fileURLWithPath: path, isDirectory: true).standardizedFileURL
        let value = try rawStaging.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey])
        guard value.isDirectory == true, value.isSymbolicLink != true else { throw failure("文件缓存路径无效") }
        // Darwin's normal /var temporary-directory prefix is an OS symlink to
        // /private/var. Resolve trusted ancestors once; reject links inside roots.
        let staging = rawStaging.resolvingSymlinksInPath()
        self.id = command.id; self.roots = roots; self.staging = staging
        super.init()
        writeQueue.name = "LinkBridge.FilePromises"
        writeQueue.qualityOfService = .userInitiated
        writeQueue.maxConcurrentOperationCount = 2
    }

    func start() throws {
        // The trusted parent will verify this exact WindowServer owner/number
        // before injecting down. Only an actual down on our own view starts drag.
        guard let quartzPoint = CGEvent(source: nil)?.location,
              let mainScreen = NSScreen.screens.first else { throw failure("无法读取系统指针和主屏幕") }
        // One Quartz read supplies both window position and IPC point. AppKit's
        // cached mouseLocation can differ while posted remote moves are pending.
        let point = NSPoint(x: quartzPoint.x, y: mainScreen.frame.maxY - quartzPoint.y)
        let window = NSWindow(contentRect: NSRect(x: point.x - 60, y: point.y - 45, width: 120, height: 90),
                              styleMask: [.borderless], backing: .buffered, defer: false)
        window.isOpaque = false; window.backgroundColor = NSColor.white.withAlphaComponent(0.015)
        window.hasShadow = false; window.level = .floating
        window.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        window.contentView = sourceView
        sourceView.drag = self
        sourceView.setAccessibilityElement(true)
        sourceView.setAccessibilityRole(.group)
        sourceView.setAccessibilityLabel("联桥文件拖放源")
        window.orderFrontRegardless()
        self.window = window
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.08) { [weak self] in
            guard let self, self.cancelled == nil, self.window === window else { return }
            self.reportSourceReady(window: window, quartzPoint: quartzPoint)
        }
    }

    private func reportSourceReady(window: NSWindow, quartzPoint: CGPoint) {
        let windows = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]] ?? []
        func describe(_ value: [String: Any]?) -> String {
            guard let value else { return "none" }
            return "pid=\(value[kCGWindowOwnerPID as String] ?? "?") window=\(value[kCGWindowNumber as String] ?? "?") bounds=\(value[kCGWindowBounds as String] ?? "?")"
        }
        let own = windows.first { ($0[kCGWindowNumber as String] as? Int) == window.windowNumber }
        let firstHit = windows.first {
            guard let bounds = $0[kCGWindowBounds as String] as? NSDictionary,
                  let rectangle = CGRect(dictionaryRepresentation: bounds) else { return false }
            return rectangle.contains(quartzPoint)
        }
        emit("source-ready", id: id, window: window.windowNumber, x: quartzPoint.x, y: quartzPoint.y,
             detail: "source pid=\(getpid()) window=\(window.windowNumber) quartz=\(quartzPoint) AppKitFrame=\(window.frame) ownCG={\(describe(own))} firstHit={\(describe(firstHit))}")
    }

    func startNative(with event: NSEvent) throws {
        guard session == nil, cancelled == nil, let window, event.windowNumber == window.windowNumber else {
            throw failure("系统文件拖放源窗口无效")
        }
        var items: [NSDraggingItem] = []
        for (index, root) in roots.enumerated() {
            let type = root.isDirectory ? UTType.folder : (UTType(filenameExtension: (root.name as NSString).pathExtension) ?? .data)
            let provider = NSFilePromiseProvider(fileType: type.identifier, delegate: self)
            provider.userInfo = index
            providers.append(provider)
            let item = NSDraggingItem(pasteboardWriter: provider)
            let image = root.isDirectory ? NSWorkspace.shared.icon(for: .folder) : NSWorkspace.shared.icon(for: type)
            image.size = NSSize(width: 48, height: 48)
            item.setDraggingFrame(NSRect(x: 60 + index % 3 * 4, y: 45 - index % 3 * 4, width: 48, height: 48), contents: image)
            items.append(item)
        }
        emit("source-down", id: id)
        session = sourceView.beginDraggingSession(with: items, event: event, source: self)
        session?.animatesToStartingPositionsOnCancelOrFail = false
        session?.draggingFormation = .pile
    }

    func markReady() {
        condition.lock(); ready = true; condition.broadcast(); condition.unlock()
        emit("upload-ready", id: id)
    }

    func cancel(_ error: String) {
        condition.lock()
        if cancelled == nil { cancelled = failure(error) }
        condition.broadcast(); condition.unlock()
        // Route Escape to our own native tracking loop, never to another app.
        if let event = NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: [],
            timestamp: ProcessInfo.processInfo.systemUptime, windowNumber: window?.windowNumber ?? 0,
            context: nil, characters: "\u{1b}", charactersIgnoringModifiers: "\u{1b}", isARepeat: false, keyCode: 53) {
            NSApp.postEvent(event, atStart: true)
        }
        if session == nil { ended = true; settle() }
    }

    func draggingSession(_ session: NSDraggingSession, sourceOperationMaskFor context: NSDraggingContext) -> NSDragOperation { .copy }
    func ignoreModifierKeys(for session: NSDraggingSession) -> Bool { true }
    func draggingSession(_ session: NSDraggingSession, willBeginAt screenPoint: NSPoint) {
        emit("started", id: id)
    }
    func draggingSession(_ session: NSDraggingSession, endedAt screenPoint: NSPoint, operation: NSDragOperation) {
        emit("native-ended", id: id, operation: operation.rawValue)
        window?.orderOut(nil)
        ended = true; accepted = operation.contains(.copy)
        if !accepted {
            condition.lock()
            if cancelled == nil { cancelled = failure("目标位置未接受文件；源文件已保留") }
            condition.broadcast(); condition.unlock()
        }
        // Promise callbacks can be queued immediately after this callback.
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.2) { self.settle() }
    }

    func filePromiseProvider(_ provider: NSFilePromiseProvider, fileNameForType fileType: String) -> String {
        guard let index = provider.userInfo as? Int, roots.indices.contains(index) else { return "invalid" }
        return roots[index].name
    }
    func operationQueue(for filePromiseProvider: NSFilePromiseProvider) -> OperationQueue { writeQueue }
    func filePromiseProvider(_ provider: NSFilePromiseProvider, writePromiseTo url: URL,
                             completionHandler: @escaping (Error?) -> Void) {
        emit("write-target", id: id, paths: [url.path])
        condition.lock(); pending += 1
        while !ready && cancelled == nil { condition.wait(until: Date(timeIntervalSinceNow: 1)) }
        let cancellation = cancelled
        condition.unlock()
        var error: Error? = cancellation
        if error == nil {
            do {
                guard let index = provider.userInfo as? Int, roots.indices.contains(index), url.isFileURL else {
                    throw failure("系统未提供有效的文件夹目标")
                }
                let root = roots[index]
                let source = staging.appendingPathComponent(root.name, isDirectory: root.isDirectory)
                emit("copy-begin", id: id, paths: [url.path], detail: "root=\(index) name=\(root.name)")
                // Finder already supplies its promised destination URL. Apple's
                // file-promise samples write directly here and then complete.
                // A second destination-coordination transaction can wait on the
                // receiver that is itself waiting for this promise to complete.
                try copyVerified(source: source, target: url, isDirectory: root.isDirectory, depth: 0, isRoot: true)
                condition.lock(); destinations.append(url.path); written += 1; condition.unlock()
                emit("copy-verified", id: id, paths: [url.path], detail: "root=\(index)")
            } catch let caught { error = caught }
        }
        condition.lock()
        if let error { failed = true; if cancelled == nil { cancelled = error }; condition.broadcast() }
        pending -= 1
        let progress = "written=\(written)/\(roots.count) pending=\(pending)"
        condition.unlock()
        emit("promise-completion", id: id, error: error?.localizedDescription, detail: progress)
        completionHandler(error)
        DispatchQueue.main.async { self.settle() }
    }

    private func checkCancellation() throws {
        condition.lock(); let error = cancelled; condition.unlock()
        if let error { throw error }
    }

    private func recordOwned(_ target: URL) throws {
        var info = stat()
        guard lstat(target.path, &info) == 0 else { throw failure("无法核对新建目标") }
        condition.lock(); ownedTargets.append(OwnedTarget(url: target, device: info.st_dev, inode: info.st_ino)); condition.unlock()
    }

    private func copyVerified(source: URL, target: URL, isDirectory: Bool, depth: Int, isRoot: Bool) throws {
        try checkCancellation()
        guard depth <= 64 else { throw failure("文件夹层级过深") }
        let properties = try source.resourceValues(forKeys: [.isDirectoryKey, .isRegularFileKey, .isSymbolicLinkKey])
        guard properties.isSymbolicLink != true,
              source.resolvingSymlinksInPath().path == source.standardizedFileURL.path,
              properties.isDirectory == isDirectory else { throw failure("不支持符号链接或特殊文件") }
        let fm = FileManager.default
        guard !fm.fileExists(atPath: target.path) else { throw failure("目标已有同名文件，请改名后重试") }
        if isDirectory {
            guard mkdir(target.path, 0o700) == 0 else { throw failure("目标已有同名文件夹，未被覆盖") }
            if isRoot { try recordOwned(target) }
            let children = try fm.contentsOfDirectory(at: source, includingPropertiesForKeys: [.isDirectoryKey, .isSymbolicLinkKey], options: [])
            for child in children {
                let values = try child.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey])
                guard values.isSymbolicLink != true else { throw failure("不支持符号链接") }
                try copyVerified(source: child, target: target.appendingPathComponent(child.lastPathComponent),
                                 isDirectory: values.isDirectory == true, depth: depth + 1, isRoot: false)
            }
        } else {
            guard properties.isRegularFile == true else { throw failure("仅支持普通文件和文件夹") }
            let inputFd = open(source.path, O_RDONLY | O_NOFOLLOW)
            guard inputFd >= 0 else { throw failure("无法读取已校验文件") }
            let input = FileHandle(fileDescriptor: inputFd, closeOnDealloc: true)
            defer { try? input.close() }
            let outputFd = open(target.path, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW, 0o600)
            guard outputFd >= 0 else { throw failure("无法创建目标文件，已有文件未被覆盖") }
            let output = FileHandle(fileDescriptor: outputFd, closeOnDealloc: true)
            if isRoot { try recordOwned(target) }
            defer { try? output.close() }
            var digest = SHA256()
            while let data = try input.read(upToCount: 1024 * 1024), !data.isEmpty {
                try checkCancellation(); digest.update(data: data); try output.write(contentsOf: data)
            }
            try output.synchronize()
            let sourceDigest = digest.finalize()
            let targetFd = open(target.path, O_RDONLY | O_NOFOLLOW)
            guard targetFd >= 0 else { throw failure("无法校验目标文件") }
            let verification = FileHandle(fileDescriptor: targetFd, closeOnDealloc: true)
            defer { try? verification.close() }
            var verificationDigest = SHA256()
            while let data = try verification.read(upToCount: 1024 * 1024), !data.isEmpty {
                try checkCancellation(); verificationDigest.update(data: data)
            }
            guard sourceDigest == verificationDigest.finalize() else { throw failure("目标文件完整性校验失败") }
        }
    }

    private func settle() {
        condition.lock()
        let done = ended && pending == 0 && (!accepted || failed || cancelled != nil || written == roots.count)
        guard done && !finished else { condition.unlock(); return }
        finished = true
        let error = cancelled; let paths = destinations; let cleanup = ownedTargets
        condition.unlock()
        if let error {
            // These roots were created with exclusive/nooverwrite semantics by us.
            // Never remove pre-existing destinations or follow a symlink.
            for target in cleanup {
                var info = stat()
                if lstat(target.url.path, &info) == 0, info.st_dev == target.device, info.st_ino == target.inode,
                   (info.st_mode & S_IFMT) != S_IFLNK {
                    try? FileManager.default.removeItem(at: target.url)
                }
            }
            emit("failed", id: id, error: error.localizedDescription)
        } else { emit("completed", id: id, paths: paths) }
        window?.orderOut(nil); window = nil; session = nil; providers.removeAll()
        emit("idle", id: id)
        onFinish?()
    }

#if FILE_DRAG_SELF_TEST
    static func selfTest() throws {
        let fm = FileManager.default
        let base = fm.temporaryDirectory.appendingPathComponent("LinkBridge.FileDragTest." + UUID().uuidString)
        let stage = base.appendingPathComponent("stage"), destination = base.appendingPathComponent("destination")
        try fm.createDirectory(at: stage, withIntermediateDirectories: true)
        try fm.createDirectory(at: destination, withIntermediateDirectories: false)
        defer { try? fm.removeItem(at: base) }
        let payload = Data((0..<2_097_189).map { UInt8($0 % 251) })
        try payload.write(to: stage.appendingPathComponent("测试 🐈.bin"))
        let folder = stage.appendingPathComponent("素材")
        try fm.createDirectory(at: folder.appendingPathComponent("empty"), withIntermediateDirectories: true)
        try Data("Nested contents".utf8).write(to: folder.appendingPathComponent("note.txt"))
        let roots = [Root(name: "测试 🐈.bin", isDirectory: false), Root(name: "素材", isDirectory: true)]
        let drag = try Drag(command: Command(command: "begin", id: UUID().uuidString, stagingPath: stage.path,
                                            roots: roots, error: nil))
        for root in roots {
            try drag.copyVerified(source: stage.appendingPathComponent(root.name), target: destination.appendingPathComponent(root.name),
                                  isDirectory: root.isDirectory, depth: 0, isRoot: true)
        }
        guard try Data(contentsOf: destination.appendingPathComponent("测试 🐈.bin")) == payload,
              fm.fileExists(atPath: destination.appendingPathComponent("素材/empty").path) else { throw failure("Copy test failed") }
        do {
            try drag.copyVerified(source: stage.appendingPathComponent("测试 🐈.bin"), target: destination.appendingPathComponent("测试 🐈.bin"),
                                  isDirectory: false, depth: 0, isRoot: true)
            throw failure("Overwrite test was not rejected")
        } catch let error where error.localizedDescription.contains("目标已有") { }
        try fm.createSymbolicLink(at: stage.appendingPathComponent("unsafe"), withDestinationURL: stage.appendingPathComponent("测试 🐈.bin"))
        do {
            try drag.copyVerified(source: stage.appendingPathComponent("unsafe"), target: destination.appendingPathComponent("unsafe"),
                                  isDirectory: false, depth: 0, isRoot: true)
            throw failure("Symlink test was not rejected")
        } catch let error where error.localizedDescription.contains("符号链接") { }
        drag.condition.lock(); drag.cancelled = failure("cancelled"); drag.condition.unlock()
        do {
            try drag.copyVerified(source: stage.appendingPathComponent("测试 🐈.bin"), target: destination.appendingPathComponent("cancel.bin"),
                                  isDirectory: false, depth: 0, isRoot: true)
            throw failure("Cancellation test was not rejected")
        } catch let error where error.localizedDescription == "cancelled" { }
        print("PASS unicode binary SHA256 readback, recursive folder, empty directory, collision preservation, symlink rejection, cancellation")
    }
#endif
}

private final class Helper: NSObject, NSApplicationDelegate {
    private let currentLock = NSLock()
    private var activeDrag: Drag?
    var current: Drag? {
        get { currentLock.lock(); defer { currentLock.unlock() }; return activeDrag }
        set { currentLock.lock(); activeDrag = newValue; currentLock.unlock() }
    }

    // AppKit can be in a native tracking/nested loop while promises wait. Only
    // the condition flag is touched here; all window/session work stays on main.
    func receiveReady(_ command: Command) -> Bool {
        guard command.command == "ready" else { return false }
        guard let current, current.id == command.id else {
            emit("failed", id: command.id, error: "拖放已取消"); return true
        }
        current.markReady()
        return true
    }
    func receive(_ command: Command) {
        do {
            switch command.command {
            case "begin":
                guard current == nil else { throw failure("另一项系统拖放尚未完成") }
                let drag = try Drag(command: command)
                drag.onFinish = { [weak self, weak drag] in if self?.current === drag { self?.current = nil } }
                current = drag
                do { try drag.start() } catch { current = nil; throw error }
            case "ready":
                guard let current, current.id == command.id else { throw failure("拖放已取消") }
                current.markReady()
            case "cancel":
                guard let current, current.id == command.id else { return }
                current.cancel(command.error ?? "文件拖放已取消")
            default: throw failure("未知文件拖放命令")
            }
        } catch {
            emit("failed", id: command.id, error: error.localizedDescription)
            if current == nil { emit("idle", id: command.id) }
        }
    }
}

#if FILE_DRAG_SELF_TEST
do { try Drag.selfTest() } catch { fputs("FAILED: \(error)\n", stderr); exit(1) }
#else
let application = NSApplication.shared
application.setActivationPolicy(.accessory)
private let helper = Helper()
application.delegate = helper
DispatchQueue.global(qos: .userInitiated).async {
    while let line = readLine() {
        guard line.utf8.count <= 1024 * 1024, let data = line.data(using: .utf8),
              let command = try? JSONDecoder().decode(Command.self, from: data) else {
            emit("invalid", error: "无效文件拖放命令"); continue
        }
        if helper.receiveReady(command) { continue }
        DispatchQueue.main.async { helper.receive(command) }
    }
    DispatchQueue.main.async {
        helper.current?.cancel("联桥已关闭")
        NSApp.terminate(nil)
    }
}
emit("initialized")
application.run()
#endif
