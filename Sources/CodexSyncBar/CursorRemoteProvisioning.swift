import Foundation

struct CursorRemoteProvisioningRequest: Codable, Equatable, Sendable {
    static let schemaVersion = 2
    static let maximumModelCount = 512
    static let maximumCatalogBytes = 2 * 1024 * 1024
    static let maximumEncodedBytes = 4 * 1024 * 1024

    let schemaVersion: Int
    let apiKey: String
    let model: String
    let codexModel: String
    let port: Int
    let bridgeToken: String
    let models: [String]
    let modelParameters: [String: CursorACPModelParameters]
    let modelRoutesJSON: String
    let nativeModels: [String]
    let catalogData: Data

    init(
        apiKey: String,
        model: String,
        codexModel: String,
        port: Int,
        bridgeToken: String,
        models: [String],
        modelParameters: [String: CursorACPModelParameters],
        modelRoutesJSON: String,
        nativeModels: [String],
        catalogData: Data) throws
    {
        self.schemaVersion = Self.schemaVersion
        self.apiKey = try CursorAPIKeyValidator.validated(apiKey)

        let preferences = try CursorBridgePreferences(
            port: port,
            model: model,
            agentPath: nil,
            bridgeToken: bridgeToken).validated()
        self.model = preferences.model
        self.port = preferences.port
        self.bridgeToken = preferences.bridgeToken

        var seen = Set<String>()
        let normalizedModels = models.filter { seen.insert($0).inserted }
        guard !normalizedModels.isEmpty,
              normalizedModels.count <= Self.maximumModelCount,
              normalizedModels.contains(model),
              normalizedModels.allSatisfy({ Self.isValidModelSlug($0) })
        else {
            throw AppError.processFailed("SSH에 전달할 Cursor 모델 목록이 올바르지 않습니다.")
        }
        self.models = normalizedModels

        guard Set(modelParameters.keys) == Set(normalizedModels),
              modelParameters.values.allSatisfy({ parameters in
                  Self.isValidModelSlug(parameters.model) &&
                      (parameters.context == nil || parameters.context == "1m") &&
                      parameters.effort != .default
              })
        else {
            throw AppError.processFailed("SSH에 전달할 Cursor 모델 설정이 올바르지 않습니다.")
        }
        self.modelParameters = modelParameters

        guard Self.isValidModelSlug(codexModel),
              let routeData = modelRoutesJSON.data(using: .utf8),
              let routes = try JSONSerialization.jsonObject(with: routeData) as? [String: Any],
              routes[codexModel] != nil,
              routes.keys.allSatisfy({ Self.isValidModelSlug($0) }),
              routes.keys.allSatisfy({ !seen.contains($0) })
        else {
            throw AppError.processFailed("SSH에 전달할 Cursor 모델 경로가 올바르지 않습니다.")
        }
        self.codexModel = codexModel
        self.modelRoutesJSON = modelRoutesJSON

        var nativeSeen = Set<String>()
        guard nativeModels.count <= Self.maximumModelCount,
              nativeModels.allSatisfy({ Self.isValidModelSlug($0) }),
              nativeModels.allSatisfy({ nativeSeen.insert($0).inserted }),
              nativeModels.allSatisfy({ routes[$0] == nil })
        else {
            throw AppError.processFailed("SSH에 전달할 Codex 기본 모델 목록이 올바르지 않습니다.")
        }
        self.nativeModels = nativeModels

        guard !catalogData.isEmpty,
              catalogData.count <= Self.maximumCatalogBytes,
              let catalog = try JSONSerialization.jsonObject(with: catalogData) as? [String: Any],
              let catalogModels = catalog["models"] as? [[String: Any]]
        else {
            throw AppError.processFailed("SSH에 전달할 Codex 모델 카탈로그가 올바르지 않습니다.")
        }
        let catalogSlugs = catalogModels.compactMap { $0["slug"] as? String }
        guard catalogSlugs.count == catalogModels.count,
              Set(catalogSlugs).count == catalogSlugs.count,
              catalogSlugs.contains(codexModel),
              Set(routes.keys).isSubset(of: Set(catalogSlugs)),
              Set(nativeModels).isSubset(of: Set(catalogSlugs))
        else {
            throw AppError.processFailed("SSH Codex 모델 카탈로그와 라우팅이 일치하지 않습니다.")
        }
        self.catalogData = catalogData
    }

    private static func isValidModelSlug(_ value: String) -> Bool {
        let bytes = Array(value.utf8)
        guard !bytes.isEmpty, bytes.count <= 128, Self.isASCIIAlphaNumeric(bytes[0]) else {
            return false
        }
        return bytes.allSatisfy { byte in
            Self.isASCIIAlphaNumeric(byte) || [46, 95, 58, 47, 45].contains(byte)
        }
    }

    private static func isASCIIAlphaNumeric(_ byte: UInt8) -> Bool {
        (48...57).contains(byte) || (65...90).contains(byte) || (97...122).contains(byte)
    }
}

struct CursorRemoteProvisioningResult: Equatable, Sendable {
    let deviceID: String
    let output: String
    let requiresCodexReload: Bool
}

struct CursorRemoteDeprovisioningResult: Equatable, Sendable {
    let deviceID: String
    let output: String
}
