package app.nina.data.remote

import kotlinx.serialization.json.Json

/**
 * Json do contrato v1: ignora campos desconhecidos (mudanças aditivas), não envia `null` explícito e coage entradas
 * inválidas para o default quando possível. Enums desconhecidos são tratados por [app.nina.domain.model.TolerantEnumSerializer].
 */
val NinaJson: Json = Json {
    ignoreUnknownKeys = true
    explicitNulls = false
    encodeDefaults = false
    coerceInputValues = true
}
