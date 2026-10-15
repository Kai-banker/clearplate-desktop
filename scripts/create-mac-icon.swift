import AppKit

// Original vector artwork, rendered locally at every macOS icon resolution.
// No external assets, fonts, network access or image generation are required.
func drawIcon() {
    let tile = NSBezierPath(roundedRect: NSRect(x: 100, y: 100, width: 824, height: 824), xRadius: 184, yRadius: 184)
    NSGraphicsContext.saveGraphicsState()
    let shadow = NSShadow(); shadow.shadowColor = NSColor.black.withAlphaComponent(0.20)
    shadow.shadowOffset = NSSize(width: 0, height: -16); shadow.shadowBlurRadius = 28; shadow.set()
    NSColor(calibratedRed: 0.10, green: 0.63, blue: 0.32, alpha: 1).setFill(); tile.fill()
    NSGraphicsContext.restoreGraphicsState()
    NSGradient(starting: NSColor(calibratedRed: 0.27, green: 0.83, blue: 0.43, alpha: 1),
               ending: NSColor(calibratedRed: 0.06, green: 0.57, blue: 0.33, alpha: 1))!.draw(in: tile, angle: -65)

    let plate = NSBezierPath(ovalIn: NSRect(x: 243, y: 243, width: 538, height: 538))
    NSGraphicsContext.saveGraphicsState()
    let plateShadow = NSShadow(); plateShadow.shadowColor = NSColor(calibratedWhite: 0, alpha: 0.16)
    plateShadow.shadowOffset = NSSize(width: 0, height: -12); plateShadow.shadowBlurRadius = 22; plateShadow.set()
    NSColor.white.setFill(); plate.fill()
    NSGraphicsContext.restoreGraphicsState()
    let rim = NSBezierPath(ovalIn: NSRect(x: 280, y: 280, width: 464, height: 464))
    NSColor(calibratedRed: 0.87, green: 0.95, blue: 0.89, alpha: 1).setStroke(); rim.lineWidth = 10; rim.stroke()

    let check = NSBezierPath(); check.move(to: NSPoint(x: 365, y: 515))
    check.line(to: NSPoint(x: 464, y: 419)); check.line(to: NSPoint(x: 650, y: 612))
    check.lineWidth = 54; check.lineCapStyle = .round; check.lineJoinStyle = .round
    NSColor(calibratedRed: 0.12, green: 0.65, blue: 0.34, alpha: 1).setStroke(); check.stroke()

    let leaf = NSBezierPath(); leaf.move(to: NSPoint(x: 621, y: 687))
    leaf.curve(to: NSPoint(x: 758, y: 807), controlPoint1: NSPoint(x: 610, y: 775), controlPoint2: NSPoint(x: 692, y: 810))
    leaf.curve(to: NSPoint(x: 621, y: 687), controlPoint1: NSPoint(x: 764, y: 727), controlPoint2: NSPoint(x: 713, y: 683))
    leaf.close(); NSColor(calibratedRed: 0.70, green: 0.94, blue: 0.48, alpha: 1).setFill(); leaf.fill()
    let vein = NSBezierPath(); vein.move(to: NSPoint(x: 625, y: 686)); vein.line(to: NSPoint(x: 717, y: 765))
    vein.lineWidth = 10; vein.lineCapStyle = .round
    NSColor(calibratedRed: 0.13, green: 0.62, blue: 0.33, alpha: 1).setStroke(); vein.stroke()
}

guard CommandLine.arguments.count == 2 else { fatalError("Usage: swift create-mac-icon.swift output.iconset") }
let folder = URL(fileURLWithPath: CommandLine.arguments[1], isDirectory: true)
try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
for points in [16, 32, 128, 256, 512] {
    for scale in [1, 2] {
        let pixels = points * scale
        let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: pixels, pixelsHigh: pixels,
            bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
            bytesPerRow: 0, bitsPerPixel: 0)!
        NSGraphicsContext.saveGraphicsState(); NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: bitmap)
        let transform = NSAffineTransform(); transform.scale(by: CGFloat(pixels) / 1024); transform.concat()
        drawIcon(); NSGraphicsContext.restoreGraphicsState()
        let suffix = scale == 2 ? "@2x" : ""
        try bitmap.representation(using: .png, properties: [:])!.write(to: folder.appendingPathComponent("icon_\(points)x\(points)\(suffix).png"))
    }
}
print("Rendered original ClearPlate icon at all ten macOS resolutions.")
