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
        XCTAssertEqual(
            catalog.variants.first(where: { $0.slug == "gpt-5.6-sol-high" })?.context,
            "1m")
        XCTAssertNil(
            catalog.variants.first(where: { $0.slug == "gpt-5.6-sol-high-fast" })?.context)
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

    func testBuildsCollapsedCodexPickerPresetsAndCompactRouteJSON() throws {
        let catalog = CursorModelCatalog(cliOutput: """
        auto - Auto (default)
        composer-2.5 - Composer 2.5
        composer-2.5-fast - Composer 2.5 Fast
        gpt-5.2-low - GPT-5.2 Low
        gpt-5.2-low-fast - GPT-5.2 Low Fast
        gpt-5.2 - GPT-5.2
        gpt-5.2-fast - GPT-5.2 Fast
        claude-opus-5-high - Opus 5 1M
        claude-opus-5-thinking-high - Opus 5 1M Thinking
        """)

        XCTAssertEqual(catalog.pickerPresets.map(\.id), [
            "syncbar-cursor/auto",
            "syncbar-cursor/composer-2.5",
            "syncbar-cursor/gpt-5.2",
            "syncbar-cursor/claude-opus-5/thinking",
        ])
        XCTAssertEqual(
            catalog.preferredPickerModelID(forFlatSlug: "gpt-5.2-low-fast"),
            "syncbar-cursor/gpt-5.2")
        XCTAssertEqual(
            catalog.preferredPickerModelID(forFlatSlug: "claude-opus-5-thinking-high"),
            "syncbar-cursor/claude-opus-5/thinking")
        XCTAssertEqual(
            catalog.preferredPickerModelID(forFlatSlug: "claude-opus-5-high"),
            "syncbar-cursor/claude-opus-5/thinking")
        XCTAssertNil(catalog.codexModelRoutes["syncbar-cursor/claude-opus-5"])
        XCTAssertNil(catalog.preferredPickerModelID(forFlatSlug: "missing"))

        let gpt = try XCTUnwrap(catalog.codexModelRoutes["syncbar-cursor/gpt-5.2"])
        XCTAssertEqual(gpt.defaultEffort, .medium)
        XCTAssertEqual(gpt.supportedEfforts, [.low, .medium])
        XCTAssertTrue(gpt.supportsFast)
        XCTAssertEqual(gpt.resolve(effort: .low), "gpt-5.2-low")
        XCTAssertEqual(gpt.resolve(effort: .medium, fast: true), "gpt-5.2-fast")

        let routeData = try XCTUnwrap(catalog.cursorRouteJSON().data(using: .utf8))
        let routeRoot = try XCTUnwrap(
            JSONSerialization.jsonObject(with: routeData) as? [String: Any])
        let auto = try XCTUnwrap(routeRoot["syncbar-cursor/auto"] as? [String: Any])
        XCTAssertEqual(auto["default_effort"] as? String, "default")
        let autoVariants = try XCTUnwrap(auto["variants"] as? [String: [String: String]])
        XCTAssertEqual(autoVariants["default"]?["standard"], "auto")

        let composer = try XCTUnwrap(
            routeRoot["syncbar-cursor/composer-2.5"] as? [String: Any])
        XCTAssertEqual(composer["default_effort"] as? String, "default")
        let composerVariants = try XCTUnwrap(
            composer["variants"] as? [String: [String: String]])
        XCTAssertEqual(composerVariants["default"]?["standard"], "composer-2.5")
        XCTAssertEqual(composerVariants["default"]?["fast"], "composer-2.5-fast")

        let gptWire = try XCTUnwrap(
            routeRoot["syncbar-cursor/gpt-5.2"] as? [String: Any])
        XCTAssertEqual(gptWire["default_effort"] as? String, "medium")
        let gptVariants = try XCTUnwrap(
            gptWire["variants"] as? [String: [String: String]])
        XCTAssertEqual(gptVariants["medium"]?["standard"], "gpt-5.2")
        XCTAssertEqual(gptVariants["medium"]?["fast"], "gpt-5.2-fast")
        XCTAssertEqual(gptVariants["low"]?["standard"], "gpt-5.2-low")
    }

    func testFiltersCodexExposureByCollapsedPickerModelID() throws {
        let catalog = CursorModelCatalog(cliOutput: """
        auto - Auto (default)
        gpt-5.6-sol-medium - GPT-5.6 Sol 1M
        gpt-5.6-sol-high-fast - GPT-5.6 Sol High Fast
        claude-opus-5-high - Opus 5 1M
        claude-opus-5-thinking-high - Opus 5 1M Thinking
        claude-opus-5-thinking-high-fast - Opus 5 Thinking High Fast
        """)

        let filtered = try catalog.exposingCodexModelIDs([
            "syncbar-cursor/gpt-5.6-sol",
            "syncbar-cursor/claude-opus-5/thinking",
        ])

        XCTAssertEqual(filtered.pickerPresets.map(\.id), [
            "syncbar-cursor/gpt-5.6-sol",
            "syncbar-cursor/claude-opus-5/thinking",
        ])
        XCTAssertEqual(filtered.variants.map(\.slug), [
            "gpt-5.6-sol-medium",
            "gpt-5.6-sol-high-fast",
            "claude-opus-5-thinking-high",
            "claude-opus-5-thinking-high-fast",
        ])
        XCTAssertNil(filtered.codexModelRoutes["syncbar-cursor/auto"])
        XCTAssertNil(filtered.codexModelRoutes["syncbar-cursor/claude-opus-5"])
        XCTAssertNotNil(filtered.codexModelRoutes["syncbar-cursor/claude-opus-5/thinking"])
        XCTAssertThrowsError(try catalog.exposingCodexModelIDs([]))
        XCTAssertThrowsError(try catalog.exposingCodexModelIDs([
            "syncbar-cursor/not-in-account",
        ]))
    }

    func testNativeFastReasoningEffortsUseOnlyStandardFastPairs() throws {
        let catalog = CursorModelCatalog(cliOutput: """
        gpt-5.4-low - GPT-5.4 1M Low
        gpt-5.4-medium - GPT-5.4 1M
        gpt-5.4-medium-fast - GPT-5.4 1M Fast
        gpt-5.4-high - GPT-5.4 1M High
        gpt-5.4-high-fast - GPT-5.4 1M High Fast
        """)

        let route = try XCTUnwrap(catalog.codexModelRoutes["syncbar-cursor/gpt-5.4"])
        XCTAssertTrue(route.supportsFast)
        XCTAssertEqual(route.defaultEffort, .medium)
        XCTAssertEqual(route.supportedEfforts, [.medium, .high])
        XCTAssertEqual(route.resolve(effort: .low), "gpt-5.4-low")
        XCTAssertNil(route.resolve(effort: .low, fast: true))
        XCTAssertEqual(route.resolve(effort: .high, fast: true), "gpt-5.4-high-fast")
    }

    func testCodexPickerOmitsIncompatibleGLMAndMixedContextFastVariants() throws {
        let catalog = CursorModelCatalog(cliOutput: """
        glm-5.2-max - GLM 5.2 Max
        gpt-5.6-sol-high - GPT-5.6 Sol 1M High
        gpt-5.6-sol-high-fast - GPT-5.6 Sol High Fast
        """)

        XCTAssertNotNil(catalog.family(baseSlug: "glm-5.2"))
        XCTAssertNil(catalog.preferredPickerModelID(forFlatSlug: "glm-5.2-max"))
        XCTAssertNil(catalog.codexModelRoutes["syncbar-cursor/glm-5.2"])

        let gpt = try XCTUnwrap(
            catalog.codexModelRoutes["syncbar-cursor/gpt-5.6-sol"])
        XCTAssertFalse(gpt.supportsFast)
        XCTAssertEqual(gpt.resolve(effort: .high), "gpt-5.6-sol-high")
        XCTAssertNil(gpt.resolve(effort: .high, fast: true))
        XCTAssertNotNil(catalog.family(containingSlug: "gpt-5.6-sol-high-fast"))
    }

    func testRejectsDuplicateNativeRouteCoordinates() throws {
        let catalog = CursorModelCatalog(cliOutput: """
        gpt-5.5-xhigh - GPT-5.5 Extra High
        gpt-5.5-extra-high - GPT-5.5 Extra High
        """)

        XCTAssertThrowsError(try catalog.cursorRouteJSON())
    }

    func testFastOnlyCohortIsOmittedWithoutBreakingOtherPickerRoutes() throws {
        let catalog = CursorModelCatalog(cliOutput: """
        future-fast - Future Fast
        composer-2.5 - Composer 2.5
        """)

        XCTAssertEqual(catalog.pickerPresets.map(\.id), [
            "syncbar-cursor/composer-2.5",
        ])
        XCTAssertNil(catalog.preferredPickerModelID(forFlatSlug: "future-fast"))
        XCTAssertNotNil(catalog.preferredPickerModelID(forFlatSlug: "composer-2.5"))
        XCTAssertNoThrow(try catalog.cursorRouteJSON())
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
        claude-opus-5-thinking-high - Opus 5 1M Thinking
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
        XCTAssertEqual(
            catalog.codexModelIDs(in: .anthropicClaude),
            ["syncbar-cursor/claude-opus-5/thinking"])
        XCTAssertEqual(
            catalog.codexModelIDs(in: .openAIGPT),
            ["syncbar-cursor/gpt-5.6-sol"])
        XCTAssertTrue(catalog.codexModelIDs(in: .googleGemini).isEmpty)
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

    func testAnthropicPickerAlwaysUsesThinkingAndMigratesLegacyExposureID() throws {
        let catalog = CursorModelCatalog(cliOutput: """
        claude-opus-5-high - Opus 5 1M High
        claude-opus-5-thinking-high - Opus 5 1M Thinking High
        claude-opus-5-thinking-high-fast - Opus 5 Thinking High Fast
        """)

        XCTAssertEqual(
            catalog.family(baseSlug: "claude-opus-5")?.preferredVariant?.slug,
            "claude-opus-5-thinking-high")
        XCTAssertEqual(
            catalog.pickerPresets.map(\.id),
            ["syncbar-cursor/claude-opus-5/thinking"])
        let migrated = try catalog.exposingCodexModelIDs([
            "syncbar-cursor/claude-opus-5",
        ])
        XCTAssertEqual(
            migrated.pickerPresets.map(\.id),
            ["syncbar-cursor/claude-opus-5/thinking"])
        XCTAssertEqual(migrated.variants.map(\.slug), [
            "claude-opus-5-thinking-high",
            "claude-opus-5-thinking-high-fast",
        ])
    }

    func testBuildsExactACPParametersWithoutInferringContextFromSlug() throws {
        let catalog = CursorModelCatalog(cliOutput: """
        gpt-5.6-sol-high-fast - GPT-5.6 Sol 1M High Fast
        composer-2.5 - Composer 2.5
        claude-opus-5-thinking-low - Opus 5 1m Low Thinking
        vendor-1m-high - Vendor 1M-preview High
        bracketed-context - Vendor (1M)
        """)

        XCTAssertEqual(
            catalog.acpModelParametersBySlug["gpt-5.6-sol-high-fast"],
            CursorACPModelParameters(
                model: "gpt-5.6-sol",
                context: "1m",
                effort: .high,
                fast: true,
                thinking: false))
        XCTAssertEqual(
            catalog.acpModelParametersBySlug["composer-2.5"],
            CursorACPModelParameters(
                model: "composer-2.5",
                context: nil,
                effort: nil,
                fast: false,
                thinking: false))
        XCTAssertEqual(
            catalog.acpModelParametersBySlug["claude-opus-5-thinking-low"],
            CursorACPModelParameters(
                model: "claude-opus-5",
                context: "1m",
                effort: .low,
                fast: false,
                thinking: true))
        XCTAssertNil(catalog.acpModelParametersBySlug["vendor-1m-high"]?.context)
        XCTAssertNil(catalog.acpModelParametersBySlug["bracketed-context"]?.context)

        let data = try XCTUnwrap(catalog.acpModelParametersJSON().data(using: .utf8))
        let object = try XCTUnwrap(
            JSONSerialization.jsonObject(with: data) as? [String: [String: Any]])
        XCTAssertEqual(object["gpt-5.6-sol-high-fast"]?["model"] as? String, "gpt-5.6-sol")
        XCTAssertEqual(object["gpt-5.6-sol-high-fast"]?["context"] as? String, "1m")
        XCTAssertEqual(object["gpt-5.6-sol-high-fast"]?["effort"] as? String, "high")
        XCTAssertEqual(object["gpt-5.6-sol-high-fast"]?["fast"] as? Bool, true)
        XCTAssertEqual(object["gpt-5.6-sol-high-fast"]?["thinking"] as? Bool, false)
        XCTAssertNil(object["composer-2.5"]?["context"])
        XCTAssertNil(object["composer-2.5"]?["effort"])
    }

    func testMapsCursorBaseSlugsToExactACPModelIDs() throws {
        let aliases = [
            "auto": "default",
            "cursor-grok-4.6": "grok-4.6",
            "cursor-grok-4.5": "grok-4.5",
            "claude-4.6-sonnet": "claude-sonnet-4-6",
            "claude-4.6-opus": "claude-opus-4-6",
            "claude-4.5-opus": "claude-opus-4-5",
            "claude-4.5-sonnet": "claude-sonnet-4-5",
            "claude-4-sonnet": "claude-sonnet-4",
        ]

        for (baseSlug, modelID) in aliases {
            XCTAssertEqual(
                CursorModelCatalog.acpModelID(forBaseSlug: baseSlug),
                modelID)
        }
        XCTAssertEqual(
            CursorModelCatalog.acpModelID(forBaseSlug: "gpt-5.6-sol"),
            "gpt-5.6-sol")

        let catalog = CursorModelCatalog(cliOutput: """
        auto - Auto (default)
        cursor-grok-4.6-high - Grok 4.6 High
        claude-4.6-sonnet-medium-thinking - Sonnet 4.6 1M Thinking
        """)
        XCTAssertEqual(catalog.acpModelParametersBySlug["auto"]?.model, "default")
        XCTAssertEqual(
            catalog.acpModelParametersBySlug["cursor-grok-4.6-high"]?.model,
            "grok-4.6")
        XCTAssertEqual(
            catalog.acpModelParametersBySlug["claude-4.6-sonnet-medium-thinking"]?.model,
            "claude-sonnet-4-6")
    }

    func testCursorModelVariantDecodesLegacyJSONWithoutContext() throws {
        let data = Data("""
        {
          "slug": "composer-2.5",
          "displayName": "Composer 2.5",
          "baseSlug": "composer-2.5",
          "effort": "default",
          "fast": false,
          "thinking": false
        }
        """.utf8)

        let variant = try JSONDecoder().decode(CursorModelVariant.self, from: data)

        XCTAssertNil(variant.context)
        XCTAssertEqual(variant.slug, "composer-2.5")
    }
}
