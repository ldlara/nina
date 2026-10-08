import XCTest
import SwiftUI
import UIKit
import NinaCore
@testable import Nina

final class ThemeTests: XCTestCase {
    func testDynamicColorsResolveToTokenValuesInBothModes() {
        for token in NinaColorToken.allCases {
            let color = UIColor(Color(token))
            let light = color.resolvedColor(with: UITraitCollection(userInterfaceStyle: .light))
            let dark = color.resolvedColor(with: UITraitCollection(userInterfaceStyle: .dark))
            XCTAssertEqual(components(light), components(UIColor(rgb: token.light)), "claro \(token)")
            XCTAssertEqual(components(dark), components(UIColor(rgb: token.dark)), "escuro \(token)")
        }
    }

    func testThemePreferenceMapping() {
        XCTAssertNil(ThemePreference.system.colorScheme)
        XCTAssertEqual(ThemePreference.light.colorScheme, .light)
        XCTAssertEqual(ThemePreference.night.colorScheme, .dark)
    }

    private func components(_ color: UIColor) -> [Int] {
        var r: CGFloat = 0, g: CGFloat = 0, b: CGFloat = 0, a: CGFloat = 0
        color.getRed(&r, green: &g, blue: &b, alpha: &a)
        return [r, g, b].map { Int(($0 * 255).rounded()) }
    }
}
