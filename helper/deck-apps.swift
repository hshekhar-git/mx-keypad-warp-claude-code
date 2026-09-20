// deck-apps — the plugin's eyes on the macOS app list.
//
//   deck-apps watch <iconDir>   prints one JSON line now and again whenever an app launches, quits,
//                               activates, hides or unhides. Exits when stdin closes, so it can never
//                               outlive the plugin that started it.
//   deck-apps hide <pid>        hides an app (what Cmd-H does).
//
// Event-driven rather than polled: the frontmost app on the keypad changes the moment it changes on
// screen, and an idle machine costs nothing.

import AppKit

let iconPixels = 144

func iconPath(for app: NSRunningApplication, in dir: String) -> String {
    let id = app.bundleIdentifier ?? "pid-\(app.processIdentifier)"
    let safe = id.map { $0.isLetter || $0.isNumber || $0 == "." || $0 == "-" ? $0 : "_" }
    let path = (dir as NSString).appendingPathComponent(String(safe) + ".png")
    if FileManager.default.fileExists(atPath: path) { return path }

    guard let icon = app.icon,
          let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: iconPixels, pixelsHigh: iconPixels,
                                     bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                                     colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)
    else { return "" }

    NSGraphicsContext.saveGraphicsState()
    let ctx = NSGraphicsContext(bitmapImageRep: rep)
    ctx?.imageInterpolation = .high
    NSGraphicsContext.current = ctx
    icon.draw(in: NSRect(x: 0, y: 0, width: iconPixels, height: iconPixels),
              from: .zero, operation: .copy, fraction: 1)
    NSGraphicsContext.restoreGraphicsState()

    guard let png = rep.representation(using: .png, properties: [:]) else { return "" }
    let tmp = path + ".tmp.\(getpid())"
    do {
        try png.write(to: URL(fileURLWithPath: tmp))
        _ = try? FileManager.default.removeItem(atPath: path)
        try FileManager.default.moveItem(atPath: tmp, toPath: path)
        return path
    } catch {
        return ""
    }
}

func snapshot(iconDir: String) -> String {
    let apps = NSWorkspace.shared.runningApplications.filter { $0.activationPolicy == .regular }
    let list: [[String: Any]] = apps.map { app in
        [
            "pid": Int(app.processIdentifier),
            "bundle": app.bundleIdentifier ?? "",
            "name": app.localizedName ?? "",
            "path": app.bundleURL?.path ?? "",
            "hidden": app.isHidden,
            "launched": app.launchDate?.timeIntervalSince1970 ?? 0,
            "icon": iconPath(for: app, in: iconDir),
        ]
    }
    let front = NSWorkspace.shared.frontmostApplication
    let root: [String: Any] = [
        "front": front?.bundleIdentifier ?? "",
        "frontPid": Int(front?.processIdentifier ?? 0),
        "apps": list,
    ]
    guard let data = try? JSONSerialization.data(withJSONObject: root, options: []),
          let line = String(data: data, encoding: .utf8)
    else { return "{}" }
    return line
}

let args = CommandLine.arguments
guard args.count >= 3 else {
    FileHandle.standardError.write("usage: deck-apps watch <iconDir> | hide <pid>\n".data(using: .utf8)!)
    exit(2)
}

switch args[1] {
case "hide":
    guard let pid = Int32(args[2]), let app = NSRunningApplication(processIdentifier: pid) else { exit(1) }
    exit(app.hide() ? 0 : 1)

case "watch":
    let iconDir = args[2]
    try? FileManager.default.createDirectory(atPath: iconDir, withIntermediateDirectories: true)
    setvbuf(stdout, nil, _IOLBF, 0)

    var last = ""
    func emit() {
        let line = snapshot(iconDir: iconDir)
        if line != last {
            last = line
            print(line)
        }
    }

    let center = NSWorkspace.shared.notificationCenter
    for name in [NSWorkspace.didLaunchApplicationNotification,
                 NSWorkspace.didTerminateApplicationNotification,
                 NSWorkspace.didActivateApplicationNotification,
                 NSWorkspace.didHideApplicationNotification,
                 NSWorkspace.didUnhideApplicationNotification] {
        center.addObserver(forName: name, object: nil, queue: .main) { _ in emit() }
    }

    // An app's activation policy settles a moment after launch; this catches what the event missed.
    Timer.scheduledTimer(withTimeInterval: 5, repeats: true) { _ in emit() }

    // The parent holds our stdin open. When it goes away - cleanly or not - so do we.
    Thread.detachNewThread {
        while FileHandle.standardInput.availableData.count > 0 {}
        exit(0)
    }

    emit()
    RunLoop.main.run()

default:
    exit(2)
}
