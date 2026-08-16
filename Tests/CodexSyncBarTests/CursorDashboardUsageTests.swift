import Foundation
import XCTest
@testable import CodexSyncBar

final class CursorDashboardUsageTests: XCTestCase {
    func testDecodesCursorUsageSummaryAndCalculatesRemainingPools() throws {
        let data = Data(
            """
            {
              "billingCycleStart": "2026-08-14T14:52:01.000Z",
              "billingCycleEnd": "2026-09-14T14:52:01.000Z",
              "membershipType": "ultra",
              "individualUsage": {
                "plan": {
                  "enabled": true,
                  "used": 2763,
                  "limit": 40000,
                  "remaining": 37237,
                  "autoPercentUsed": 0.9665,
                  "apiPercentUsed": 1.66,
                  "totalPercentUsed": 1.1052
                }
              }
            }
            """.utf8)

        let snapshot = try CursorDashboardUsageDecoder.decode(data)

        XCTAssertEqual(snapshot.membershipType, "ultra")
        XCTAssertEqual(snapshot.cursorModelsRemainingPercent, 99)
        XCTAssertEqual(snapshot.otherModelsRemainingPercent, 98)
        XCTAssertEqual(snapshot.totalRemainingPercent, 99)
        XCTAssertEqual(snapshot.billingCycleEnd.timeIntervalSince1970, 1_789_397_521, accuracy: 0.1)
    }

    func testRejectsDisabledOrMalformedUsageSummary() {
        let disabled = Data(
            """
            {
              "billingCycleStart": "2026-08-14T14:52:01.000Z",
              "billingCycleEnd": "2026-09-14T14:52:01.000Z",
              "membershipType": "ultra",
              "individualUsage": {"plan": {
                "enabled": false, "used": 0, "limit": 1, "remaining": 1,
                "autoPercentUsed": 0, "apiPercentUsed": 0, "totalPercentUsed": 0
              }}
            }
            """.utf8)
        XCTAssertThrowsError(try CursorDashboardUsageDecoder.decode(disabled))
        XCTAssertThrowsError(try CursorDashboardUsageDecoder.decode(Data("{}".utf8)))
    }
}
