import XCTest
@testable import CodexSyncBar

final class CursorModelCatalogTests: XCTestCase {
    func testParsesCLIOutputAndSeparatesOpenAIGPTFromOpenAICodex() throws {
        let catalog = CursorModelCatalog(cliOutput: """
        Available models

        auto - Auto (default)
        gpt-5.3-codex-low - Codex 5.3 Low
        gpt-5.3-codex - Codex 5.3
        gpt-5.6-sol-high - GPT-5.6 Sol 1M High
        gpt-5.6-sol-high-fast - GPT-5.6 Sol High Fast
        gpt-5.2 - GPT-5.2

        Tip: use --model <id> to switch.
        """)

        XCTAssertEqual(catalog.variants.count, 6)
        XCTAssertEqual(catalog.family(baseSlug: "auto")?.group, .automatic)
        XCTAssertEqual(catalog.family(baseSlug: "gpt-5.3-codex")?.group, .openAICodex)
        XCTAssertEqual(catalog.family(baseSlug: "gpt-5.6-sol")?.group, .openAIGPT)
        XCTAssertEqual(catalog.family(baseSlug: "gpt-5.2")?.group, .openAIGPT)
        XCTAssertNotEqual(
            CursorModelGroup.openAIGPT.displayName,
            CursorModelGroup.openAICodex.displayName)
        XCTAssertEqual(catalog.family(baseSlug: "gpt-5.3-codex")?.displayName, "Codex 5.3")
        XCTAssertEqual(catalog.family(baseSlug: "gpt-5.6-sol")?.displayName, "GPT-5.6 Sol")
    }

    func testCollectsEffortFastAndThinkingVariantsAndResolvesExactSlugs() throws {
        let catalog = CursorModelCatalog(cliOutput: """
        gpt-5.6-sol-none - GPT-5.6 Sol 1M None
        gpt-5.6-sol-medium - GPT-5.6 Sol 1M
        gpt-5.6-sol-high-fast - GPT-5.6 Sol High Fast
        claude-opus-5-thinking-low - Opus 5 1M Low Thinking
        claude-opus-5-thinking-low-fast - Opus 5 1M Low Thinking Fast
        claude-opus-5-thinking-max - Opus 5 1M Max Thinking
        claude-4.6-sonnet-medium - Sonnet 4.6 1M
        claude-4.6-sonnet-medium-thinking - Sonnet 4.6 1M Thinking
        """)

        let gpt = try XCTUnwrap(catalog.family(baseSlug: "gpt-5.6-sol"))
        XCTAssertEqual(gpt.availableEfforts, [.none, .medium, .high])
        XCTAssertTrue(gpt.supportsFast)
        XCTAssertFalse(gpt.supportsThinking)
        XCTAssertEqual(
            catalog.resolve(baseSlug: "gpt-5.6-sol", effort: .high, fast: true),
            "gpt-5.6-sol-high-fast")
        XCTAssertNil(catalog.resolve(baseSlug: "gpt-5.6-sol", effort: .low))

        let opus = try XCTUnwrap(catalog.family(baseSlug: "claude-opus-5"))
        XCTAssertEqual(opus.availableEfforts(fast: false, thinking: true), [.low, .max])
        XCTAssertEqual(
            catalog.resolve(CursorModelSelection(
                baseSlug: "claude-opus-5",
                effort: .low,
                fast: true,
                thinking: true)),
            "claude-opus-5-thinking-low-fast")
        XCTAssertEqual(
            catalog.resolve(
                baseSlug: "claude-4.6-sonnet",
                effort: .medium,
                thinking: true),
            "claude-4.6-sonnet-medium-thinking")
    }

    func testMapsExtraHighAndXHighSpellingsToOneEffortWithoutInventingSlugs() throws {
        let catalog = CursorModelCatalog(cliOutput: """
        gpt-5.5-extra-high - GPT-5.5 1M Extra High
        gpt-5.5-extra-high-fast - GPT-5.5 Extra High Fast
        gpt-5.6-terra-xhigh - GPT-5.6 Terra 1M Extra High
        """)

        XCTAssertEqual(
            catalog.resolve(baseSlug: "gpt-5.5", effort: .xhigh),
            "gpt-5.5-extra-high")
        XCTAssertEqual(
            catalog.resolve(baseSlug: "gpt-5.5", effort: .xhigh, fast: true),
            "gpt-5.5-extra-high-fast")
        XCTAssertEqual(
            catalog.resolve(baseSlug: "gpt-5.6-terra", effort: .xhigh),
            "gpt-5.6-terra-xhigh")
        XCTAssertNil(catalog.resolve(baseSlug: "gpt-5.5", effort: .high))
    }

    func testPreservesAutoAndWellFormedUnclassifiedModelsWhileIgnoringUnknownLines() throws {
        let catalog = CursorModelCatalog(cliOutput: """
        Available models
        auto - Auto (default)
        vendor.custom/model:preview - Vendor Preview
        missing separator
         - Missing Slug
        invalid slug - Invalid Slug
        valid-slug -
        duplicate - First Name
        duplicate - Second Name
        """)

        XCTAssertEqual(catalog.variants.map(\.slug), [
            "auto",
            "vendor.custom/model:preview",
            "duplicate",
        ])
        XCTAssertEqual(catalog.family(baseSlug: "auto")?.displayName, "Auto")
        XCTAssertEqual(catalog.family(baseSlug: "vendor.custom/model:preview")?.group, .other)
        XCTAssertEqual(
            catalog.resolve(baseSlug: "vendor.custom/model:preview"),
            "vendor.custom/model:preview")
        XCTAssertEqual(
            catalog.family(baseSlug: "duplicate")?.variants.first?.displayName,
            "First Name")
    }

    func testRoundTripsEveryVariantThroughSelection() throws {
        let catalog = CursorModelCatalog(cliOutput: """
        composer-2.5 - Composer 2.5
        composer-2.5-fast - Composer 2.5 Fast
        gemini-3.6-flash-minimal - Gemini 3.6 Flash Minimal
        claude-4.5-sonnet-thinking - Sonnet 4.5 Thinking
        glm-5.2-max - GLM 5.2 Max
        """)

        for variant in catalog.variants {
            XCTAssertEqual(catalog.resolve(variant.selection), variant.slug)
            XCTAssertEqual(catalog.selection(forSlug: variant.slug), variant.selection)
            XCTAssertEqual(catalog.family(containingSlug: variant.slug)?.baseSlug, variant.baseSlug)
        }

        XCTAssertEqual(catalog.family(baseSlug: "composer-2.5")?.availableEfforts, [.default])
        XCTAssertEqual(
            catalog.selection(forSlug: "claude-4.5-sonnet-thinking"),
            CursorModelSelection(
                baseSlug: "claude-4.5-sonnet",
                effort: .default,
                thinking: true))
    }

    func testSectionsUseStableGroupOrderAndContainOnlyPresentGroups() throws {
        let catalog = CursorModelCatalog(cliOutput: """
        claude-opus-5-high - Opus 5 1M
        gpt-5.6-sol-high - GPT-5.6 Sol 1M High
        auto - Auto (default)
        gpt-5.3-codex-high - Codex 5.3 High
        composer-2.5 - Composer 2.5
        """)

        XCTAssertEqual(catalog.sections.map(\.group), [
            .automatic,
            .cursor,
            .openAIGPT,
            .openAICodex,
            .anthropicClaude,
        ])
    }

    func testPreferredVariantChoosesDefaultThenMediumWithoutEnablingOptions() throws {
        let catalog = CursorModelCatalog(cliOutput: """
        gpt-5.6-sol-high-fast - GPT-5.6 Sol High Fast
        gpt-5.6-sol-high - GPT-5.6 Sol 1M High
        gpt-5.6-sol-medium - GPT-5.6 Sol 1M
        composer-2.5-fast - Composer 2.5 Fast
        composer-2.5 - Composer 2.5
        """)

        XCTAssertEqual(
            catalog.family(baseSlug: "gpt-5.6-sol")?.preferredVariant?.slug,
            "gpt-5.6-sol-medium")
        XCTAssertEqual(
            catalog.family(baseSlug: "composer-2.5")?.preferredVariant?.slug,
            "composer-2.5")
    }
}
