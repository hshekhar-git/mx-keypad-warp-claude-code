// swift tools/make-icon.swift <out.png> — draws the 256x256 plugin icon: a 3x3 keypad whose keys
// are lit in the deck's state colours.
import AppKit

let out = CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "Icon256x256.png"
let size = 256
let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: size, pixelsHigh: size, bitsPerSample: 8,
                           samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                           colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
NSGraphicsContext.saveGraphicsState()
NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)

func rgb(_ hex: Int) -> NSColor {
    NSColor(srgbRed: CGFloat((hex >> 16) & 0xFF) / 255, green: CGFloat((hex >> 8) & 0xFF) / 255,
            blue: CGFloat(hex & 0xFF) / 255, alpha: 1)
}

rgb(0x141618).setFill()
NSBezierPath(roundedRect: NSRect(x: 0, y: 0, width: size, height: size), xRadius: 52, yRadius: 52).fill()

// Rows are drawn bottom-up.
let keys = [0x3A3D42, 0x3A3D42, 0x3A3D42,
            0x2F7D4A, 0x55585C, 0xB85535,
            0xB85535, 0xB82F2F, 0x2F7D4A]
let pad: CGFloat = 30, gap: CGFloat = 12
let cell = (CGFloat(size) - 2 * pad - 2 * gap) / 3
for (i, hex) in keys.enumerated() {
    let x = pad + CGFloat(i % 3) * (cell + gap)
    let y = pad + CGFloat(i / 3) * (cell + gap)
    rgb(hex).setFill()
    NSBezierPath(roundedRect: NSRect(x: x, y: y, width: cell, height: cell), xRadius: 12, yRadius: 12).fill()
}

NSGraphicsContext.restoreGraphicsState()
try! rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: out))
