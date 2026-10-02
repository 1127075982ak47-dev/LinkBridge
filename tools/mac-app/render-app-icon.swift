import Foundation
import CoreGraphics
import ImageIO
import CryptoKit

// Windows and Mac render the same 256-unit geometric design independently.
// The manifest ties both source geometry and ICO to this generation.
struct IconError: LocalizedError {
    let message: String
    var errorDescription: String? { message }
}
struct Layer: Decodable { let radius: Double; let from: String; let to: String; let stroke: String; let strokeWidth: Double }
struct Reflection: Decodable {
    let color: String; let radius: Double; let begin: Double; let end: Double; let width: Double
    init(from decoder: Decoder) throws {
        var c = try decoder.unkeyedContainer()
        color = try c.decode(String.self); radius = try c.decode(Double.self)
        begin = try c.decode(Double.self); end = try c.decode(Double.self); width = try c.decode(Double.self)
    }
}
struct Screen: Decodable { let x: Double; let y: Double; let w: Double; let h: Double }
struct Design: Decodable { let layers: [Layer]; let screens: [Screen]; let screenStrokeWidth: Double; let start: [Double]; let curves: [[Double]]; let glyph: String; let glyphWidth: Double; let reflection: Reflection }
struct Manifest: Decodable { let IconSha256: String; let DesignSha256: String }
var design: Design!
func paint(_ value: String, _ space: CGColorSpace) throws -> CGColor {
    let raw = String(value.dropFirst())
    guard value.first == "#", raw.count == 6 || raw.count == 8, let bits = UInt64(raw, radix: 16) else { throw IconError(message: "Invalid design color.") }
    let rgba = raw.count == 6 ? bits << 8 | 255 : bits
    return CGColor(colorSpace: space, components: [CGFloat((rgba >> 24) & 255) / 255, CGFloat((rgba >> 16) & 255) / 255, CGFloat((rgba >> 8) & 255) / 255, CGFloat(rgba & 255) / 255])!
}
let sizes: [(String, Int)] = [
    ("icon_16x16.png", 16), ("icon_16x16@2x.png", 32),
    ("icon_32x32.png", 32), ("icon_32x32@2x.png", 64),
    ("icon_128x128.png", 128), ("icon_128x128@2x.png", 256),
    ("icon_256x256.png", 256), ("icon_256x256@2x.png", 512),
    ("icon_512x512.png", 512), ("icon_512x512@2x.png", 1024)
]

func render(size: Int, to url: URL) throws {
    guard let colorSpace = CGColorSpace(name: CGColorSpace.sRGB),
          let context = CGContext(data: nil, width: size, height: size,
              bitsPerComponent: 8, bytesPerRow: size * 4, space: colorSpace,
              bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue | CGBitmapInfo.byteOrder32Big.rawValue)
    else { throw IconError(message: "Unable to create an RGBA icon canvas.") }
    context.setShouldAntialias(true)
    context.setAllowsAntialiasing(true)
    context.clear(CGRect(x: 0, y: 0, width: size, height: size))
    context.translateBy(x: 0, y: CGFloat(size))
    let scale = CGFloat(size) / 256
    context.scaleBy(x: scale, y: -scale)
    for layer in design.layers {
        let rect = CGRect(x: 128-layer.radius, y: 128-layer.radius, width: layer.radius*2, height: layer.radius*2)
        context.saveGState(); context.addEllipse(in: rect); context.clip()
        let gradient = CGGradient(colorsSpace: colorSpace, colors: [try paint(layer.from, colorSpace), try paint(layer.to, colorSpace)] as CFArray, locations: [0, 1])!
        context.drawLinearGradient(gradient, start: CGPoint(x: 0, y: 0), end: CGPoint(x: 0, y: 256), options: [])
        context.restoreGState()
        context.setStrokeColor(try paint(layer.stroke, colorSpace)); context.setLineWidth(layer.strokeWidth); context.strokeEllipse(in: rect)
    }
    context.setLineCap(.round); context.setLineJoin(.round)
    context.setStrokeColor(try paint("#FFFFFF", colorSpace)); context.setLineWidth(design.screenStrokeWidth)
    for s in design.screens {
        context.addPath(CGPath(roundedRect:CGRect(x:s.x,y:s.y,width:s.w,height:s.h), cornerWidth:6, cornerHeight:6, transform:nil)); context.strokePath()
        let x = s.x+s.w/2, bottom = s.y+s.h
        context.move(to:CGPoint(x:x,y:bottom)); context.addLine(to:CGPoint(x:x,y:bottom+15)); context.strokePath()
        context.move(to:CGPoint(x:x-21,y:bottom+17)); context.addLine(to:CGPoint(x:x+21,y:bottom+17)); context.strokePath()
    }
    func glyph(_ offset: Double, _ color: String, _ width: Double) throws {
        context.move(to: CGPoint(x: design.start[0], y: design.start[1]+offset))
        for c in design.curves { context.addCurve(to: CGPoint(x: c[4],y:c[5]+offset), control1: CGPoint(x:c[0],y:c[1]+offset), control2: CGPoint(x:c[2],y:c[3]+offset)) }
        context.setStrokeColor(try paint(color, colorSpace)); context.setLineWidth(width); context.strokePath()
    }
    try glyph(2, "#103491", design.glyphWidth+2)
    try glyph(0, design.glyph, design.glyphWidth)
    let r = design.reflection
    context.addArc(center: CGPoint(x:128,y:128), radius:r.radius, startAngle:r.begin * .pi/180, endAngle:r.end * .pi/180, clockwise:false)
    context.setStrokeColor(try paint(r.color, colorSpace)); context.setLineWidth(r.width); context.strokePath()
    guard let image = context.makeImage(),
          let destination = CGImageDestinationCreateWithURL(url as CFURL, "public.png" as CFString, 1, nil)
    else { throw IconError(message: "Unable to encode icon \(url.lastPathComponent).") }
    CGImageDestinationAddImage(destination, image, nil)
    guard CGImageDestinationFinalize(destination) else {
        throw IconError(message: "PNG encoding failed for \(url.lastPathComponent).")
    }
}

do {
    guard CommandLine.arguments.count == 3 else {
        throw IconError(message: "Usage: swift render-app-icon.swift <existing linkbridge.ico> <output.iconset>")
    }
    let sourceURL = URL(fileURLWithPath: CommandLine.arguments[1])
    let source = try Data(contentsOf: sourceURL)
    let hash = SHA256.hash(data: source).map { String(format: "%02x", $0) }.joined()
    let directorySource = sourceURL.deletingLastPathComponent()
    let designData = try Data(contentsOf: directorySource.appendingPathComponent("linkbridge-icon-design.json"))
    let manifest = try JSONDecoder().decode(Manifest.self, from: Data(contentsOf: directorySource.appendingPathComponent("linkbridge-icon-manifest.json")))
    let designHash = SHA256.hash(data: designData).map { String(format: "%02x", $0) }.joined()
    guard hash == manifest.IconSha256 && designHash == manifest.DesignSha256 else {
        throw IconError(message: "Windows logo source changed. Update its native vector definition; do not upscale the old ICO.")
    }
    design = try JSONDecoder().decode(Design.self, from: designData)
    guard design.start.count == 2, design.curves.allSatisfy({$0.count == 6}) else { throw IconError(message:"Invalid geometric path.") }
    let directory = URL(fileURLWithPath: CommandLine.arguments[2], isDirectory: true)
    guard directory.pathExtension == "iconset" else {
        throw IconError(message: "Output must be a .iconset directory.")
    }
    try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
    for (name, size) in sizes { try render(size: size, to: directory.appendingPathComponent(name)) }
    print("Rendered 10 sRGB RGBA iconset PNGs, 16 to 1024 px, from the existing brand's native paths.")
} catch {
    fputs("Icon generation failed: \(error.localizedDescription)\n", stderr)
    exit(1)
}
