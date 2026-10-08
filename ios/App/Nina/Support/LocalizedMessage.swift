import Foundation
import NinaCore

/// Converte as chaves do NinaCore (`UserMessage`) em texto localizado do bundle do app.
enum LocalizedMessage {
    static func string(for message: UserMessage, bundle: Bundle = .main) -> String {
        let format = bundle.localizedString(forKey: message.key, value: nil, table: nil)
        guard !message.arguments.isEmpty else { return format }
        return String(format: format, locale: Locale.current, arguments: message.arguments.map { $0 as CVarArg })
    }

    static func text(for message: UserMessage) -> String { string(for: message) }

    static func plural(_ key: String, count: Int, bundle: Bundle = .main) -> String {
        let format = bundle.localizedString(forKey: key, value: nil, table: nil)
        return String.localizedStringWithFormat(format, count)
    }
}
