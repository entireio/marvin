import SceneKit
import simd

/// Diagnostic shadow geometry: identical static town triangles, welded only by
/// exact position, with separate single/double-sided material groups. The main
/// camera keeps its original geometry, materials, tangents and LODs.
final class TownShadowBatch {
    private struct Pair {
        let original:SCNNode,proxy:SCNNode
        var castsShadow:Bool
    }
    private var pairs:[Pair]=[]
    private var pairIndices:[ObjectIdentifier:Int]=[:]
    private let camera:SCNCamera
    private let cameraMask:Int
    private let lights:[(SCNLight,Int)]
    private var enabled=false
    private(set) var inputVertices=0,outputVertices=0,inputElements=0,outputElements=0
    private(set) var triangles=0
    private var cache:[ObjectIdentifier:SCNGeometry]=[:]
    private static let proxyBit=1 << 20
    private let single=SCNMaterial(),double=SCNMaterial()
    enum Failure:Error { case unsupportedGeometry }

    init(root:SCNNode,camera:SCNCamera)throws {
        self.camera=camera;cameraMask=camera.categoryBitMask
        var lights:[(SCNLight,Int)]=[],nodes:[SCNNode]=[]
        root.enumerateChildNodes { node,_ in
            if let light=node.light,light.castsShadow { lights.append((light,light.categoryBitMask)) }
            if node.name?.hasPrefix("Town cell ")==true { nodes.append(node) }
        }
        self.lights=lights
        single.lightingModel = .constant;double.lightingModel = .constant;double.isDoubleSided=true
        for node in nodes {
            guard let geometry=node.geometry else { throw Failure.unsupportedGeometry }
            let proxy=SCNNode(geometry:try build(geometry));proxy.simdTransform=node.simdTransform;proxy.pivot=node.pivot
            proxy.name="Town shadow batch";proxy.categoryBitMask=Self.proxyBit;proxy.isHidden=true
            node.parent!.addChildNode(proxy)
            pairIndices[ObjectIdentifier(node)]=pairs.count
            pairs.append(Pair(original:node,proxy:proxy,castsShadow:node.castsShadow))
        }
        guard !pairs.isEmpty else { throw Failure.unsupportedGeometry }
    }
    deinit {
        setEnabled(false)
        for pair in pairs { pair.proxy.removeFromParentNode() }
    }
    func setEnabled(_ value:Bool) {
        guard value != enabled else { return }
        enabled=value
        camera.categoryBitMask=value ? cameraMask & ~Self.proxyBit:cameraMask
        for (light,mask) in lights { light.categoryBitMask=value ? mask | Self.proxyBit:mask }
        for i in pairs.indices {
            if value {
                pairs[i].castsShadow=pairs[i].original.castsShadow
                pairs[i].original.castsShadow=false
                pairs[i].proxy.isHidden = !pairs[i].castsShadow
                pairs[i].proxy.castsShadow=pairs[i].castsShadow
            } else {
                pairs[i].original.castsShadow=pairs[i].castsShadow;pairs[i].proxy.isHidden=true
            }
        }
    }
    /// Apply the town's authoritative caster decision every frame. Originals
    /// stay disabled while the shadow copy follows distance/frustum visibility.
    @discardableResult func setCaster(_ original:SCNNode,enabled value:Bool)->Bool {
        guard enabled,let i=pairIndices[ObjectIdentifier(original)] else { return false }
        pairs[i].castsShadow=value
        if original.castsShadow { original.castsShadow=false }
        let proxy=pairs[i].proxy
        if proxy.castsShadow != value { proxy.castsShadow=value }
        if proxy.isHidden == value { proxy.isHidden = !value }
        return true
    }
    private func build(_ geometry:SCNGeometry)throws -> SCNGeometry {
        if let existing=cache[ObjectIdentifier(geometry)] { return existing }
        let allowed=[CityMaterials.plaster,CityMaterials.adobe,CityMaterials.cloth,CityMaterials.metal]
        guard !geometry.materials.isEmpty,geometry.materials.allSatisfy({ m in allowed.contains(where:{$0 === m}) }),
              let source=geometry.sources(for:.vertex).first,source.usesFloatComponents,
              source.componentsPerVector==3,[4,8].contains(source.bytesPerComponent) else { throw Failure.unsupportedGeometry }
        var vertices:[SCNVector3]=[],lookup:[SIMD3<Float>:Int32]=[:],remap:[Int32]=[]
        try source.data.withUnsafeBytes { bytes in
            for i in 0..<source.vectorCount {
                var point=SIMD3<Float>.zero
                for c in 0..<3 {
                    let offset=source.dataOffset+i*source.dataStride+c*source.bytesPerComponent
                    let value=source.bytesPerComponent==4 ? Double(bytes.loadUnaligned(fromByteOffset:offset,as:Float.self)):bytes.loadUnaligned(fromByteOffset:offset,as:Double.self)
                    guard value.isFinite,Double(Float(value))==value else { throw Failure.unsupportedGeometry }
                    point[c]=Float(value)
                }
                if let index=lookup[point] { remap.append(index) }
                else { let index=Int32(vertices.count);lookup[point]=index;remap.append(index);vertices.append(SCNVector3(point)) }
            }
        }
        var groups=[[Int32](),[Int32]()]
        for (i,element) in geometry.elements.enumerated() {
            guard element.primitiveType == .triangles,[1,2,4].contains(element.bytesPerIndex) else { throw Failure.unsupportedGeometry }
            let data=element.data
            let group=geometry.materials[i%geometry.materials.count].isDoubleSided ? 1:0
            try data.withUnsafeBytes { bytes in
                for j in 0..<element.primitiveCount*3 {
                    let old:Int = {
                        let offset=j*element.bytesPerIndex
                        if element.bytesPerIndex==1 { return Int(bytes.loadUnaligned(fromByteOffset:offset,as:UInt8.self)) }
                        if element.bytesPerIndex==2 { return Int(bytes.loadUnaligned(fromByteOffset:offset,as:UInt16.self)) }
                        return Int(bytes.loadUnaligned(fromByteOffset:offset,as:UInt32.self))
                    }()
                    guard remap.indices.contains(old) else { throw Failure.unsupportedGeometry }
                    groups[group].append(remap[old])
                }
            }
            triangles += element.primitiveCount
        }
        let active=groups.indices.filter{!groups[$0].isEmpty}
        let result=SCNGeometry(sources:[SCNGeometrySource(vertices:vertices)],elements:active.map{SCNGeometryElement(indices:groups[$0],primitiveType:.triangles)})
        result.materials=active.map{$0==0 ? single:double}
        cache[ObjectIdentifier(geometry)]=result
        inputVertices += source.vectorCount;outputVertices += vertices.count
        inputElements += geometry.elements.count;outputElements += active.count
        result.levelsOfDetail=try geometry.levelsOfDetail?.map { level in
            guard let original=level.geometry else { throw Failure.unsupportedGeometry }
            let geometry=try build(original)
            return level.screenSpaceRadius>0 ? SCNLevelOfDetail(geometry:geometry,screenSpaceRadius:level.screenSpaceRadius):SCNLevelOfDetail(geometry:geometry,worldSpaceDistance:level.worldSpaceDistance)
        }
        return result
    }
    // Telemetry must not query hundreds of SceneKit properties during a drive.
    var telemetryStatistics:[String:Int] {
        ["enabled":enabled ? 1:0,"activeProxies":enabled ? pairs.filter{$0.castsShadow}.count:0,
         "nodes":pairs.count,"inputVertices":inputVertices,"outputVertices":outputVertices,
         "inputElements":inputElements,"outputElements":outputElements,"triangles":triangles]
    }
    var statistics:[String:Int] { ["enabled":enabled ? 1:0,"activeProxies":pairs.filter{!$0.proxy.isHidden && $0.proxy.castsShadow}.count,"originalCasters":pairs.filter{$0.original.castsShadow}.count,"nodes":pairs.count,"inputVertices":inputVertices,"outputVertices":outputVertices,"inputElements":inputElements,"outputElements":outputElements,"triangles":triangles] }
}
