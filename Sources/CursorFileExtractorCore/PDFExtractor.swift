import AppKit
import Foundation
import PDFKit

public enum PDFRenderDetail: String, Sendable {
    case low
    case auto
    case high

    public var maximumLongEdge: Int {
        switch self {
        case .low:
            return 1_024
        case .auto, .high:
            return 1_600
        }
    }
}

public struct PDFExtractionLimits: Sendable {
    public var maximumInputBytes: Int
    public var maximumPages: Int
    public var maximumTextBytes: Int
    public var maximumImageBytes: Int
    public var maximumTotalImageBytes: Int

    public init(
        maximumInputBytes: Int = 12 * 1_024 * 1_024,
        maximumPages: Int = 16,
        maximumTextBytes: Int = 2 * 1_024 * 1_024,
        maximumImageBytes: Int = 8 * 1_024 * 1_024,
        maximumTotalImageBytes: Int = 24 * 1_024 * 1_024
    ) {
        self.maximumInputBytes = maximumInputBytes
        self.maximumPages = maximumPages
        self.maximumTextBytes = maximumTextBytes
        self.maximumImageBytes = maximumImageBytes
        self.maximumTotalImageBytes = maximumTotalImageBytes
    }

    public static let `default` = PDFExtractionLimits()
}

public struct PDFExtractedPage: Codable, Equatable, Sendable {
    public let page: Int
    public let mimeType: String
    public let data: String

    public init(page: Int, mimeType: String = "image/png", data: String) {
        self.page = page
        self.mimeType = mimeType
        self.data = data
    }

    private enum CodingKeys: String, CodingKey {
        case page
        case mimeType = "mime_type"
        case data
    }
}

public struct PDFExtractionResult: Codable, Equatable, Sendable {
    public let text: String
    public let pageCount: Int
    public let pages: [PDFExtractedPage]

    public init(text: String, pageCount: Int, pages: [PDFExtractedPage]) {
        self.text = text
        self.pageCount = pageCount
        self.pages = pages
    }

    private enum CodingKeys: String, CodingKey {
        case text
        case pageCount = "page_count"
        case pages
    }
}

public enum PDFExtractionError: Error, Equatable, Sendable {
    case inputTooLarge
    case invalidPDF
    case encryptedPDF
    case invalidPageCount
    case extractedTextTooLarge
    case pageRenderingFailed
    case pageImageTooLarge
    case totalPageImagesTooLarge
}

public enum PDFExtractor {
    public static func extract(
        _ data: Data,
        detail: PDFRenderDetail = .auto,
        limits: PDFExtractionLimits = .default
    ) throws -> PDFExtractionResult {
        guard limits.maximumInputBytes >= 0,
              limits.maximumPages > 0,
              limits.maximumTextBytes >= 0,
              limits.maximumImageBytes >= 0,
              limits.maximumTotalImageBytes >= 0 else {
            throw PDFExtractionError.invalidPDF
        }
        guard data.count <= limits.maximumInputBytes else {
            throw PDFExtractionError.inputTooLarge
        }
        guard let document = PDFDocument(data: data) else {
            throw PDFExtractionError.invalidPDF
        }
        guard !document.isEncrypted, !document.isLocked else {
            throw PDFExtractionError.encryptedPDF
        }
        guard document.pageCount > 0, document.pageCount <= limits.maximumPages else {
            throw PDFExtractionError.invalidPageCount
        }

        var pageTexts: [String] = []
        pageTexts.reserveCapacity(document.pageCount)
        var textByteCount = 0

        for index in 0..<document.pageCount {
            guard let page = document.page(at: index) else {
                throw PDFExtractionError.invalidPDF
            }
            let pageText = page.string ?? ""
            if index > 0 {
                textByteCount = try checkedSum(textByteCount, 2)
            }
            textByteCount = try checkedSum(textByteCount, pageText.utf8.count)
            guard textByteCount <= limits.maximumTextBytes else {
                throw PDFExtractionError.extractedTextTooLarge
            }
            pageTexts.append(pageText)
        }

        var pages: [PDFExtractedPage] = []
        pages.reserveCapacity(document.pageCount)
        var totalImageBytes = 0

        for index in 0..<document.pageCount {
            guard let page = document.page(at: index),
                  let pngData = renderedPNG(page: page, maximumLongEdge: detail.maximumLongEdge) else {
                throw PDFExtractionError.pageRenderingFailed
            }
            guard pngData.count <= limits.maximumImageBytes else {
                throw PDFExtractionError.pageImageTooLarge
            }
            totalImageBytes = try checkedSum(totalImageBytes, pngData.count)
            guard totalImageBytes <= limits.maximumTotalImageBytes else {
                throw PDFExtractionError.totalPageImagesTooLarge
            }
            pages.append(PDFExtractedPage(page: index + 1, data: pngData.base64EncodedString()))
        }

        return PDFExtractionResult(
            text: pageTexts.joined(separator: "\n\n"),
            pageCount: document.pageCount,
            pages: pages
        )
    }

    private static func checkedSum(_ lhs: Int, _ rhs: Int) throws -> Int {
        let (sum, overflow) = lhs.addingReportingOverflow(rhs)
        guard !overflow else {
            throw PDFExtractionError.invalidPDF
        }
        return sum
    }

    private static func renderedPNG(page: PDFPage, maximumLongEdge: Int) -> Data? {
        guard maximumLongEdge > 0 else { return nil }
        let bounds = page.bounds(for: .mediaBox)
        guard bounds.width.isFinite, bounds.height.isFinite,
              bounds.width > 0, bounds.height > 0 else {
            return nil
        }

        let scale = CGFloat(maximumLongEdge) / max(bounds.width, bounds.height)
        let targetSize = NSSize(
            width: max(1, floor(bounds.width * scale)),
            height: max(1, floor(bounds.height * scale))
        )

        return autoreleasepool {
            let image = page.thumbnail(of: targetSize, for: .mediaBox)
            guard let tiffData = image.tiffRepresentation,
                  let bitmap = NSBitmapImageRep(data: tiffData) else {
                return nil
            }
            return bitmap.representation(using: .png, properties: [:])
        }
    }
}
