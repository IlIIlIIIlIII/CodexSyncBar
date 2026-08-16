import CursorFileExtractorCore
import Darwin
import Foundation

private let maximumInputBytes = 12 * 1_024 * 1_024
private let maximumOutputBytes = 36 * 1_024 * 1_024

private enum CLIError: Error {
    case invalidArguments
    case inputReadFailed
    case inputTooLarge
    case outputTooLarge
}

private func selectedDetail(arguments: [String]) throws -> PDFRenderDetail {
    guard !arguments.isEmpty else { return .auto }
    guard arguments.count == 2,
          arguments[0] == "--detail",
          let detail = PDFRenderDetail(rawValue: arguments[1]) else {
        throw CLIError.invalidArguments
    }
    return detail
}

private func readBoundedStandardInput() throws -> Data {
    var input = Data()
    input.reserveCapacity(min(maximumInputBytes, 64 * 1_024))

    do {
        while true {
            let remaining = maximumInputBytes - input.count
            let requestedBytes = min(64 * 1_024, remaining + 1)
            guard let chunk = try FileHandle.standardInput.read(upToCount: requestedBytes),
                  !chunk.isEmpty else {
                return input
            }
            guard chunk.count <= remaining else {
                throw CLIError.inputTooLarge
            }
            input.append(chunk)
        }
    } catch let error as CLIError {
        throw error
    } catch {
        throw CLIError.inputReadFailed
    }
}

private func applyResourceLimits() {
    setLimit(resource: RLIMIT_CPU, soft: 30, hard: 35)
    setLimit(resource: RLIMIT_FSIZE, soft: 32 * 1_024 * 1_024, hard: 32 * 1_024 * 1_024)
    setLimit(resource: RLIMIT_NOFILE, soft: 32, hard: 32)
}

private func setLimit(resource: Int32, soft: rlim_t, hard: rlim_t) {
    var limit = rlimit(rlim_cur: soft, rlim_max: hard)
    _ = withUnsafePointer(to: &limit) { pointer in
        setrlimit(resource, pointer)
    }
}

private func run() throws {
    applyResourceLimits()
    let detail = try selectedDetail(arguments: Array(CommandLine.arguments.dropFirst()))
    let input = try readBoundedStandardInput()
    let result = try PDFExtractor.extract(input, detail: detail)
    let output = try JSONEncoder().encode(result)
    guard output.count <= maximumOutputBytes else {
        throw CLIError.outputTooLarge
    }
    try FileHandle.standardOutput.write(contentsOf: output)
}

do {
    try run()
} catch {
    FileHandle.standardError.write(Data("PDF extraction failed\n".utf8))
    exit(EXIT_FAILURE)
}
