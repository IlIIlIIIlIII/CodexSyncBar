import XCTest
@testable import CodexSyncBar

final class CursorAPIKeyStoreTests: XCTestCase {
    func testValidationAcceptsUTF8ByteBoundariesWithoutGuessingPrefix() throws {
        let minimum = String(repeating: "x", count: CursorAPIKeyValidator.minimumByteCount)
        let maximum = String(repeating: "y", count: CursorAPIKeyValidator.maximumByteCount)
        let opaque = "arbitrary-opaque-key"

        XCTAssertEqual(try CursorAPIKeyValidator.validated(minimum), minimum)
        XCTAssertEqual(try CursorAPIKeyValidator.validated(maximum), maximum)
        XCTAssertEqual(try CursorAPIKeyValidator.validated(opaque), opaque)
    }

    func testValidationCountsUTF8BytesInsteadOfCharacters() throws {
        let minimum = String(repeating: "😀", count: 4)
        let maximum = String(repeating: "😀", count: 256)

        XCTAssertEqual(minimum.count, 4)
        XCTAssertEqual(minimum.utf8.count, CursorAPIKeyValidator.minimumByteCount)
        XCTAssertEqual(try CursorAPIKeyValidator.validated(minimum), minimum)
        XCTAssertEqual(maximum.utf8.count, CursorAPIKeyValidator.maximumByteCount)
        XCTAssertEqual(try CursorAPIKeyValidator.validated(maximum), maximum)
    }

    func testValidationRejectsValuesOutsideUTF8ByteBounds() {
        let tooShort = String(repeating: "x", count: CursorAPIKeyValidator.minimumByteCount - 1)
        let tooLong = String(repeating: "😀", count: 257)

        XCTAssertThrowsError(try CursorAPIKeyValidator.validated(tooShort)) { error in
            XCTAssertEqual(
                error as? CursorAPIKeyValidationError,
                .invalidByteCount(actual: CursorAPIKeyValidator.minimumByteCount - 1))
        }
        XCTAssertThrowsError(try CursorAPIKeyValidator.validated(tooLong)) { error in
            XCTAssertEqual(
                error as? CursorAPIKeyValidationError,
                .invalidByteCount(actual: 1_028))
        }
    }

    func testValidationRejectsWhitespaceControlAndNULScalars() {
        let invalidValues = [
            "abcdefgh ijklmnop",
            "abcdefgh\tijklmnop",
            "abcdefgh\nijklmnop",
            "abcdefgh\u{00A0}ijklmnop",
            "abcdefgh\u{007F}ijklmnop",
            "abcdefgh\u{0000}ijklmnop",
        ]

        for value in invalidValues {
            XCTAssertThrowsError(
                try CursorAPIKeyValidator.validated(value),
                "Expected rejection for \(value.debugDescription)")
        }
    }

    func testValidationRejectsInvisibleUnicodeFormatCharacters() {
        for scalar in ["\u{200B}", "\u{200C}", "\u{2060}"] {
            let value = "abcdefgh\(scalar)ijklmnop"
            XCTAssertThrowsError(try CursorAPIKeyValidator.validated(value)) { error in
                XCTAssertEqual(
                    error as? CursorAPIKeyValidationError,
                    .containsFormatCharacter,
                    "Expected a format-character error for \(scalar.debugDescription)")
            }
        }
    }

    func testSDKLoginResultParsesStructuredCredentialAndRejectsExpiredValues() throws {
        let key = "cursor_" + String(repeating: "a", count: 32)
        let now = Date(timeIntervalSince1970: 1)
        let data = Data("""
        {"schema_version":1,"api_key":"\(key)","email":"subscriber@example.com","api_key_expires_at_ms":2000}
        """.utf8)

        let credential = try CursorSDKCredential(loginResultData: data, now: now)

        XCTAssertEqual(credential.apiKey, key)
        XCTAssertEqual(credential.email, "subscriber@example.com")
        XCTAssertEqual(credential.expiresAt, Date(timeIntervalSince1970: 2))
        XCTAssertFalse(credential.isExpired(at: now))
        XCTAssertEqual(try credential.usableAPIKey(at: now), key)
        XCTAssertThrowsError(try credential.usableAPIKey(at: credential.expiresAt)) { error in
            XCTAssertEqual(error as? CursorSDKCredentialValidationError, .expired)
        }
        XCTAssertThrowsError(try CursorSDKCredential(
            loginResultData: data,
            now: credential.expiresAt)) { error in
                XCTAssertEqual(error as? CursorSDKCredentialValidationError, .expired)
        }
    }

    func testSDKLoginResultRejectsMalformedSchemaAndEmail() {
        let key = "cursor_" + String(repeating: "b", count: 32)
        XCTAssertThrowsError(try CursorSDKCredential(
            loginResultData: Data("""
            {"schema_version":2,"api_key":"\(key)","email":"subscriber@example.com","api_key_expires_at_ms":2000}
            """.utf8),
            now: Date(timeIntervalSince1970: 1))) { error in
                XCTAssertEqual(error as? CursorSDKCredentialValidationError, .invalidLoginResult)
        }
        XCTAssertThrowsError(try CursorSDKCredential(
            apiKey: key,
            email: "bad email@example.com",
            apiKeyExpiresAtMilliseconds: 2_000,
            now: Date(timeIntervalSince1970: 1))) { error in
                XCTAssertEqual(error as? CursorSDKCredentialValidationError, .invalidEmail)
        }
    }

    func testKeychainIdentityIsDedicatedToCursorSDKSubscriptionCredentials() {
        XCTAssertEqual(SystemCursorSDKCredentialStore.service, "com.sunggu.codexsyncbar.cursor")
        XCTAssertEqual(SystemCursorSDKCredentialStore.account, "sdk-subscription-credential-v1")
        XCTAssertEqual(SystemCursorSDKCredentialStore.legacyAccount, "user-api-key")
    }
}
