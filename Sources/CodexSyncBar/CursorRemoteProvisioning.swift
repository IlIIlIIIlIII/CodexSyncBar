import Foundation

struct CursorRemoteProvisioningRequest: Codable, Equatable, Sendable {
    static let schemaVersion = 1
    static let maximumModelCount = 512
    static let maximumEncodedBytes = 512 * 1024

    let schemaVersion: Int
    let apiKey: String
    let model: String
    let port: Int
    let bridgeToken: String
    let models: [String]
    let modelParameters: [String: CursorACPModelParameters]

    init(
        apiKey: String,
        model: String,
        port: Int,
        bridgeToken: String,
        models: [String],
        modelParameters: [String: CursorACPModelParameters]) throws
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
}

struct CursorRemoteDeprovisioningResult: Equatable, Sendable {
    let deviceID: String
    let output: String
}
