import Foundation
import NinaCore

/// Converte as chaves do NinaCore (`UserMessage`) em texto localizado do bundle do app.
enum LocalizedMessage {
    private static let missing = "\u{1}__missing__\u{1}"

    static func string(for message: UserMessage, bundle: Bundle = .main) -> String {
        var format = bundle.localizedString(forKey: message.key, value: missing, table: nil)
        if format == missing {
            // Código de erro/campo que o servidor adicionou depois deste build: mensagem genérica, nunca a chave crua.
            let fallbackKey = message.key.hasPrefix("validation.") ? "validation.invalid" : "error.generic"
            format = bundle.localizedString(forKey: fallbackKey, value: nil, table: nil)
            return format
        }
        guard !message.arguments.isEmpty else { return format }
        return String(format: format, locale: Locale.current, arguments: message.arguments.map { $0 as CVarArg })
    }

    static func text(for message: UserMessage) -> String { string(for: message) }

    static func plural(_ key: String, count: Int, bundle: Bundle = .main) -> String {
        let format = bundle.localizedString(forKey: key, value: nil, table: nil)
        return String.localizedStringWithFormat(format, count)
    }
}
