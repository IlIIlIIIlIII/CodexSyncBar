import AppKit
import CoreGraphics
import PDFKit
import XCTest
@testable import CursorFileExtractorCore

final class PDFExtractorTests: XCTestCase {
    func testExtractsTextAndRendersOneBasedPNGPages() throws {
        let input = try makePDF(pageTexts: ["First page", "Second page"])

        let result = try PDFExtractor.extract(input, detail: .low)

        XCTAssertEqual(result.pageCount, 2)
        XCTAssertTrue(result.text.contains("First page"))
        XCTAssertTrue(result.text.contains("Second page"))
        XCTAssertEqual(result.pages.map(\.page), [1, 2])
        XCTAssertTrue(result.pages.allSatisfy { $0.mimeType == "image/png" })

        for page in result.pages {
            let data = try XCTUnwrap(Data(base64Encoded: page.data))
            XCTAssertEqual(Array(data.prefix(8)), [137, 80, 78, 71, 13, 10, 26, 10])
            let bitmap = try XCTUnwrap(NSBitmapImageRep(data: data))
            let longEdge = max(bitmap.pixelsWide, bitmap.pixelsHigh)
            XCTAssertGreaterThanOrEqual(longEdge, 1_023)
            XCTAssertLessThanOrEqual(longEdge, 1_024)
        }
    }

    func testResultUsesStableSnakeCaseJSONContract() throws {
        let result = try PDFExtractor.extract(try makePDF(pageTexts: ["Contract"]))
        let object = try XCTUnwrap(
            JSONSerialization.jsonObject(with: JSONEncoder().encode(result)) as? [String: Any]
        )

        XCTAssertEqual(object["page_count"] as? Int, 1)
        XCTAssertNil(object["pageCount"])
        let pages = try XCTUnwrap(object["pages"] as? [[String: Any]])
        XCTAssertEqual(pages[0]["page"] as? Int, 1)
        XCTAssertEqual(pages[0]["mime_type"] as? String, "image/png")
        XCTAssertNotNil(pages[0]["data"] as? String)
    }

    func testRejectsInvalidPDF() {
        XCTAssertThrowsError(try PDFExtractor.extract(Data("not a pdf".utf8))) { error in
            XCTAssertEqual(error as? PDFExtractionError, .invalidPDF)
        }
    }

    func testRejectsInputAboveLimit() throws {
        let input = try makePDF(pageTexts: ["Input limit"])
        var limits = PDFExtractionLimits.default
        limits.maximumInputBytes = input.count - 1

        XCTAssertThrowsError(try PDFExtractor.extract(input, limits: limits)) { error in
            XCTAssertEqual(error as? PDFExtractionError, .inputTooLarge)
        }
    }

    func testRejectsPageCountAboveLimit() throws {
        let input = try makePDF(pageTexts: ["One", "Two"])
        var limits = PDFExtractionLimits.default
        limits.maximumPages = 1

        XCTAssertThrowsError(try PDFExtractor.extract(input, limits: limits)) { error in
            XCTAssertEqual(error as? PDFExtractionError, .invalidPageCount)
        }
    }

    func testRejectsExtractedTextAboveLimit() throws {
        let input = try makePDF(pageTexts: ["Text limit"])
        var limits = PDFExtractionLimits.default
        limits.maximumTextBytes = 2

        XCTAssertThrowsError(try PDFExtractor.extract(input, limits: limits)) { error in
            XCTAssertEqual(error as? PDFExtractionError, .extractedTextTooLarge)
        }
    }

    func testRejectsPageImageAboveLimit() throws {
        let input = try makePDF(pageTexts: ["Image limit"])
        var limits = PDFExtractionLimits.default
        limits.maximumImageBytes = 1

        XCTAssertThrowsError(try PDFExtractor.extract(input, limits: limits)) { error in
            XCTAssertEqual(error as? PDFExtractionError, .pageImageTooLarge)
        }
    }

    func testRejectsTotalPageImagesAboveLimit() throws {
        let input = try makePDF(pageTexts: ["Total image limit"])
        var limits = PDFExtractionLimits.default
        limits.maximumTotalImageBytes = 1

        XCTAssertThrowsError(try PDFExtractor.extract(input, limits: limits)) { error in
            XCTAssertEqual(error as? PDFExtractionError, .totalPageImagesTooLarge)
        }
    }

    func testRejectsEncryptedPDF() throws {
        let plainData = try makePDF(pageTexts: ["Secret"])
        let document = try XCTUnwrap(PDFDocument(data: plainData))
        let encryptedData = try XCTUnwrap(document.dataRepresentation(options: [
            PDFDocumentWriteOption.ownerPasswordOption: "owner-password",
            PDFDocumentWriteOption.userPasswordOption: "user-password",
        ]))

        XCTAssertThrowsError(try PDFExtractor.extract(encryptedData)) { error in
            XCTAssertEqual(error as? PDFExtractionError, .encryptedPDF)
        }
    }

    private func makePDF(pageTexts: [String]) throws -> Data {
        let mutableData = NSMutableData()
        let consumer = try XCTUnwrap(CGDataConsumer(data: mutableData as CFMutableData))
        var mediaBox = CGRect(x: 0, y: 0, width: 612, height: 792)
        let context = try XCTUnwrap(CGContext(consumer: consumer, mediaBox: &mediaBox, nil))

        for text in pageTexts {
            context.beginPDFPage(nil)
            let graphicsContext = NSGraphicsContext(cgContext: context, flipped: false)
            NSGraphicsContext.saveGraphicsState()
            NSGraphicsContext.current = graphicsContext
            NSAttributedString(
                string: text,
                attributes: [
                    .font: NSFont.systemFont(ofSize: 24),
                    .foregroundColor: NSColor.black,
                ]
            ).draw(at: NSPoint(x: 72, y: 700))
            NSGraphicsContext.restoreGraphicsState()
            context.endPDFPage()
        }
        context.closePDF()
        return mutableData as Data
    }
}
